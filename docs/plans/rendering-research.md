# Rendering research

This page records what a research round on Puck's renderer found and what it
proposes to build. Nine research passes each took one area of the renderer.
Each read that area's code, collected what has been measured there, and
judged published work from outside Puck against it. The nine areas are:

- the mesh bake, which turns authored shapes into triangle meshes, with its
  levels of detail and its GPU memory;
- global illumination, the sky and exposure;
- shadows;
- visibility and draw submission;
- shading, the temporal resolve (the pass that builds each frame's image from
  several jittered frames) and the final image;
- glass, water, portals and views nested inside other views;
- GPU work scheduling and the thread that builds each frame;
- foliage, instancing and terrain;
- presentation, which is how a finished frame reaches the display.

The round follows two of the owner's rulings
([AGENTS.md](../../AGENTS.md#puck-is-pre-release)). A research round comes
before a major area of work. And Puck is free to combine techniques, not only
to copy them, so each decision weighs an approach only Puck can take beside
the borrowed one.

This page decides questions that cross those areas. It follows the manner of
[the MMO foundations memo](mmo-foundations/synthesis.md): each decision gives
the options, the evidence, what each option fixes and where it fails, the
option we favour, the strongest case against it, and the ruling it rests on.

It replaces no existing plan. [Illumination](illumination.md),
[visibility](visibility.md), [virtual geometry](virtual-geometry.md),
[streaming and memory](streaming-and-memory.md),
[transmission](transmission.md), [author shaders](author-shaders.md),
[the terrain pass](terrain-pass.md), [dense landscapes](dense-landscapes.md),
[the UI overhaul](ui.md), [the benchmark](benchmark.md) and
[the job system](engine-job-system.md) keep their own steps. This page says
four things those plans cannot say alone: where two of them meet, where the
round's evidence changes a step's priority, which mechanism can serve several
areas, and in what order the cross-area work depends on itself.
[Open items](open-items.md#next) ranks the work against everything else.
This page is not a queue.

## Summary

### What is wrong today

- **The frame is too slow on the CPU before the GPU matters.** One thread
  runs the simulation step and then builds the frame. On the lead machine
  under Vulkan the step takes about 8 ms and building the frame about 17 ms,
  where 60 frames a second allows 16.7 ms for both. Only about 1 ms of the
  17 has a breakdown by part, and the step has no breakdown by phase at all
  ([decisions 1 to 3](#1-measure-before-building)).
- **Indirect light follows a lighting change minutes late.** After a switch
  to night, the finest lighting data was up to 44 seconds old. The GPU work
  is small. The one run that measured this shows three causes and does not
  say how much each contributes. Each lighting job takes about 25 ms to go
  through the frame's queue and back, for 1 to 2 ms of GPU work. Relights
  that the change made stale wait behind ray traces that are still owed: a
  second switch in the same run, with no traces owed, relit the finest level
  in about 3 seconds. And the lighting did not register the change at all
  until about 46 seconds after the day was set. The automatic exposure
  meters that stale light, so a night scene is exposed as if it were day
  ([decisions 4](#4-run-background-gpu-work-without-waiting-a-frame-for-each-job)
  and [7](#7-make-a-lighting-change-cost-what-changed)).
- **Lighting and shadows look wrong almost everywhere, and three of the
  causes are defects, not tuning.** Linear light values are shown as if they
  were already encoded for the display
  ([decision 5](#5-convert-scene-light-to-display-values-exactly-once)). A
  shadow's softness jumps about seven-fold where the near shadow map hands
  over to the far one, and an unlit moon holds a shadow map all day
  ([decision 6](#6-give-every-receiver-the-same-shadow-answer)). Views
  through portals and onto screens get no shadow maps and no ambient
  occlusion at any size
  ([decision 14](#14-treat-a-nested-view-as-the-same-view-at-another-size)).
- **A change to bake code costs a whole bake, and the whole bake is just
  over its figure.** The wait that matters most is the one between making a
  change and seeing it. An edit to a creation rebakes only that creation's
  meshes. An edit to the bake's own code, in C# or in a shader, rebakes
  every mesh, because one identity covers the whole mesh store. Baking every
  mesh of the debug world from an empty store takes 60.6 s on Direct3D 12
  and 64.7 s on Vulkan. The figure of about a minute exists to stop that
  time drifting, not as a finish line. Six of the fourteen blend exhibits
  fail the mesh audit
  ([decision 10](#10-make-a-bake-cost-what-changed-and-close-the-contour-defects)).
- **GPU memory is never given back, and a mesh that has not arrived is a
  hole in the picture** ([decision 9](#9-give-memory-back-and-never-show-a-hole)).
- **Every measurement comes from one class of GPU.** Nothing has been
  measured on the RTX 2060 or the Steam Deck. Every gain ranked on this page
  is an assumption on those devices, and nothing says how far either is from
  its frame-rate bar.

### What we propose to do first

1. **Measure, and build what the measuring needs.** The benchmark's route
   has three chapters today, and the checks on this page name chapters that
   do not exist yet. The benchmark also does not apply the quality setting
   the frame-rate bar is defined at. So the first stage builds the route,
   states once how the bar is read, and takes the missing measurements:
   where the 17 ms of frame building goes, when each frame was actually
   displayed, what limits the cold bake, how long each kind of edit takes to
   show, and how far the RTX 2060 and the Steam Deck are from their bars. It
   changes nothing a player sees ([stage 0](#stage-0-measure)).
2. **Fix what the player sees everywhere.** Convert scene light to display
   values once, in every path, and retune the debug world to the corrected
   values in the same piece of work. Give the main shadow maps to the light
   that is actually lit, and let the lighting probes answer where no shadow
   map reaches. Let lighting jobs overlap, inside a budget that follows the
   GPU time the frame actually leaves free. Fix the bake's contour defects.
   Make what a nested view gives up an authored table instead of two engine
   constants ([stage 1](#stage-1-fix-what-the-player-sees-everywhere)).
3. **Then the frame thread and the draw list.** Capture and prepare only
   what a frame will show. Split the simulation step across a thread pool
   without adding a frame of input latency. Shrink the list of draw commands
   to the draws that survive culling
   ([stage 2](#stage-2-the-frame-thread-and-the-round-trips)).
4. **Then the shared structures that scale.** One table of instances and one
   visibility test for every pass, and one chain of meshes per shape from
   fine to coarse, serve the camera, the shadow maps, nested views and
   missing meshes ([stage 3](#stage-3-one-instance-table-and-one-chain-of-meshes)
   and [stage 4](#stage-4-scale)).

### What we are unsure of

- Where most of the frame-building time goes. The order of the frame-thread
  work follows that measurement, not this page.
- Where the simulation step's time goes. The planned split of the step may
  not reach the parts that cost most in the debug world.
- Whether the cold bake is limited by its processors or by waiting on the
  GPU. Two fixes are ready, and one measured run decides which goes first.
- How far the RTX 2060 and the Steam Deck are from their bars, and whether
  the gains this page plans can close that distance. The first stage sizes
  it. If the planned gains cannot close it for a device, the order is
  re-cut for that device and what remains goes to the owner.
- Three designs have no direct precedent outside Puck that we could find: a
  shadow filter sized in metres and read from a depth pyramid, a relight
  that stores light per light source and rescales it, and a chain of meshes
  in which every step is measured against the authored shape. Each decision
  names the nearest published relative and gives the design its own check
  before it is adopted.
- Three reviews have read this page, and none of them ran anything. All
  three read code, documents and sources. No World was booted and no
  measurement was taken for any of them
  ([what the attack and the second review changed](#what-the-attack-and-the-second-review-changed)).

### What we need from the owner

- **Three calls about what the engine does.** Each has a default the work
  proceeds on, so none blocks a step.
  - What a bar device does if the planned gains cannot bring it to its bar.
    Until the owner says otherwise it keeps the highest quality, misses the
    bar, and reports each slow frame with its cause.
  - The window size the bar is read at on the two desktop devices. Until
    the owner names one, it is the benchmark's own default window of 1280 by
    720, with a second reading at 1920 by 1080 so the cost of a larger
    window is known.
  - Whether sharpening may act on bloom's scattered light. The register
    rules that it does not, and this page keeps that.
- **Hardware, earlier than this page first said.** An RTX 2060 and a Steam
  Deck for the device measurements. The Deck is needed beside the first
  stage, because Puck's windowed Linux path has never run on hardware. An
  HDR display is needed for the colour check.
- **A comparison of two build orders.** The owner agreed to the summary's
  priorities before any reviewer read the page. The reviews moved work
  between stages and left the sequence of stages as it was. The order is a
  corrected proposal until the owner has compared it with the draft's
  ([what changed in the build order](#what-changed-in-the-build-order)).
- **No look decisions.** The tone curve, exposure, a light's softness, the
  look of night and the quality tables are values the standard package and
  any author set over a neutral engine default
  ([authored looks](#looks-are-authored)).
- Further calls come to the owner only if a measurement or another plan
  raises them ([the owner's calls](#the-owners-calls)).

## How to read this page

### Words this page uses

A reader new to the renderer needs these terms. Each is explained again
where a decision first leans on it.

| Term | What it means here |
|---|---|
| Tick | One step of the simulation. The debug world ticks 30 times a second. |
| Frame thread | The one thread that runs the tick and then builds the frame (`HostLoop.Run`). |
| Produce | Everything the frame thread does to build one frame and hand it to the GPU. |
| View | One rendering of a world from one camera. The root view fills the window. A nested view is shown inside another: through a portal or window, or on a screen in the world. |
| Residency | The GPU-side copy of one world: its instance tables, its baked meshes and its lighting data (`SdfWorldResidency`). |
| Pass | One step of drawing a frame on the GPU, such as drawing the meshes or one shadow map. |
| Kernel | A compute shader: a program the GPU runs over many pixels or records at once. |
| Bake | Puck's shapes are authored as signed distance fields. The bake samples a field and builds a triangle mesh from it. A cold bake starts from an empty mesh store. |
| Group | The unit the bake works on: a set of shapes baked into one mesh. |
| Tier | The cell size a group is baked at. A coarser tier is a new mesh built from the field on a coarser grid. |
| Level | A simplified copy of one baked mesh, made by collapsing its edges. Each mesh carries two coarser levels. |
| Floor | A group's coarsest acceptable tier, kept in GPU memory at all times so there is always something to draw. |
| Cascade | One of the shadow maps a directional light uses. There is a near one, a far one, and one for a second light. |
| Probe | A point in a 3D grid that stores the indirect light arriving there. The grid is the irradiance volume. |
| Trace | Finding, for each probe, which surfaces its rays hit. The hits depend on geometry only and are cached. |
| Relight | Shading a probe's cached hits again with the current lights. |
| Lockstep | A mode in which each frame waits for its simulation, bake and lighting work to finish, so that two runs give the same picture. Running the lighting to completion this way is called a drain. |
| Round trip | The CPU submits a GPU job, waits for it, and reads the result back before it can submit the next. |
| Bar devices | The three devices the frame-rate bar is set on: the RTX 2060 and RTX 4070 at 60 frames a second (16.7 ms a frame), and the Steam Deck at 45 (22.2 ms). |
| The recipe | The one way a bar is measured: on a quiet machine with the display on and composing ([streaming and memory](streaming-and-memory.md#how-the-bars-are-read)). |
| Route, chapter | The benchmark plays a scripted walk through the debug world (`worlds/debug/benchmark-route.puck`). A chapter is one named leg of it, and each chapter is reported on its own. |
| Preset | A named set of quality settings. The frame-rate bar is defined at the `world.quality high` preset. |
| The register | The record of settled rendering decisions ([decisions](../decisions/rendering.md)). |

### How evidence is marked

Each decision ends with an evidence note. It sorts the decision's claims by
what they rest on:

- **Code read for this page.** The page reopened the code or document in the
  current tree. The quoted words and values are what the tool printed. A
  `file:line` is a pointer at this revision and may drift.
- **From an area's map.** Taken from a research pass's map or judgement of
  its area and not reopened for this page.
- **Measured.** A measurement from the lead machine, kept in the commit or
  plan passage that recorded it. Each is an assumption until it is measured
  again under the recipe, and none qualifies a device.
- **Reasoned.** This page's own reasoning or arithmetic.
- **Outside sources.** A claim is *confirmed* when a verification pass found
  the quoted words in the source. It is *partial* when the source says
  something narrower, and the note states the narrowing. It is *unverified*
  when the source was read only through a summarizing tool or an abstract,
  or was not checked.

A confirmed claim covers one quotation. It says nothing about whether the
technique fits Puck. Published figures keep their own hardware and predict
nothing for Puck.

### Names for steps in other plans

This page points at steps other plans own. The table says what each short
name stands for, so the text can use plain words and keep the name in
parentheses.

| Name | Plan | What the step is |
|---|---|---|
| C0 | [Job system](engine-job-system.md#6-build-order) | Give the console and input a guaranteed turn in every loop iteration, and bound every wait. |
| J1 | Job system | One thread pool for the engine's CPU work, where a fork returns an operation its forking thread owns. |
| T2, T3 | Job system | Run two parts of the tick in parallel: rigid bodies (T2) and the crossing scan (T3). |
| R1 | Job system | Record several views' GPU commands in parallel. |
| V1a, V1b | [Visibility](visibility.md) | Compact draw commands to the survivors (V1a), then group them by geometry and level on a stable identity (V1b). |
| V2a, V2b | Visibility | Update far placements less often (V2a), and upload only the instances that changed (V2b). |
| V3a | Visibility | Remove triangles the bake can prove are redundant. |
| V5 | [Visibility](visibility.md#v5-multi-view-and-shadow-work) | Share work between several views and the shadow passes. |
| M2 | Visibility | The measurement of whether the meadow is limited by geometry on the RTX 2060 and the Steam Deck. |
| D1, D3, D4, D8 | [Virtual geometry](virtual-geometry.md) | Its decisions on the unit of detail (D1), a hierarchy above the draw (D3), clusters inside a draw (D4) and shadows (D8). |
| G3a, G3b, G7, G9 | Virtual geometry | Its steps: bounding boxes for skinned draws (G3a, G3b), one mesh baked over many static copies (G7, a "stand parent"), and the cluster experiment (G9). |
| `transmit-identity`, `transmit-plane` | [Transmission](transmission.md#implementation-steps) | The first step gives each shadow-map texel its body's identity and two depth layers. The second rewrites how a receiver resolves its shadow from them. |
| Stage 8 | [Benchmark](benchmark.md) | Report how long each frame stood on the display. |

## How the research was checked

The nine passes summarized 166 sources. 140 could be fetched and 26 could
not. A verification pass then reopened the claims that support each area's
first three candidates: 63 claims from 35 outside sources, and 12 claims
about 10 files in this repository.

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

Of the 75 claims checked, 23 came back partial, refuted or unreachable. Three
limits follow from how the check was done:

- Most sources were fetched through a summarizing tool. A source the
  verification pass did not reach is known only by its summary.
- The pass covered each area's first three candidates. Every candidate
  ranked below third rests on claims nobody checked.
- Both refuted claims were about this repository, and neither is used on
  this page. The unreachable claim was a quotation from a slide deck.

### Where the research disagreed, and what we did about it

The areas sometimes disagreed with the code, with each other or with an
existing plan. Twelve of those disagreements changed this page.

1. **Two areas disagree on where the tick should run.** The presentation
   area's first candidate moves the tick to its own thread, which publishes a
   snapshot for the frame to draw. The scheduling area rejects running the
   tick alongside frame building. [Decision 3](#3-keep-the-tick-ahead-of-the-frame-and-make-it-shorter)
   settles it against adding a frame of latency, on a ruling in the register.
   One open item still calls the move "the presentation view's last cut". It
   is out of date against the job system plan.
2. **Nobody has measured what limits the cold bake under the recipe.** Three
   records disagree. One sampled trace showed the bake's workers busy for 237
   of about 660 thread-seconds and called the bake "neither CPU- nor
   GPU-bound but paced by takes". A take is one GPU sampling request the bake
   waits for. Runs made with the display composing, beside other work on the
   machine, showed the bake limited by its processors and sharing them. The
   quiet runs of that same record had the display off and the GPU at idle
   clocks, where background work completes at the compositor's pace. The
   bake's work packet says CPU-bound in its header. None of these is a quiet
   machine with the display on and composing, which is what the recipe
   requires. So neither bake fix is ranked ahead of the other until one such
   run shows which is on the critical path
   ([decision 10](#10-make-a-bake-cost-what-changed-and-close-the-contour-defects)).
3. **Two records disagree on how much the coarser levels save.** The
   mesh-bake reference and the handbook say the first two levels keep 11% and
   6% of the stored triangles. A later cold bake's completion line reads 29%
   and 19% of the merged triangles. This page uses neither.
   [Decision 8](#8-choose-detail-by-one-error-budget-along-one-chain-of-meshes)
   starts by adding a counter.
4. **A deeper chain of levels was proposed, and it has already been measured
   and rejected.** The shadows area asks for more collapse levels for distant
   shadow casters. The virtual geometry plan already measured four collapse
   levels down to 128 cells, where an oak fell only from 3,052 triangles to
   1,440, and ran three experiments on field-built levels. Decision 8 gives
   the far shadow map a coarser tier instead.
5. **Indexed draws were proposed three times.** The bake, visibility and
   shadows areas each listed them as a separate candidate. They are one
   experiment ([decision 11](#11-keep-one-instance-table-and-test-it-once-for-every-pass)).
   The streaming prototype's "indexed read" cost is a different thing: it
   measured buffers reached through a descriptor array, not an index buffer.
6. **Two premises were refuted and are dropped.** One said the depth pyramid
   is recorded for a view whose quality turns ambient occlusion off, because
   no pass reads the flag. The surface pass and the views filter do read it.
   The other said open items records a research gap that the file does not
   hold.
7. **The cost of recording has two figures.** Recording is the CPU work of
   writing a frame's GPU commands. The job system plan's opening says it is
   a third of produce. Its build order says about a millisecond, since
   staging moved out of recording. The newer figure holds for recording.
   Neither explains the rest of produce
   ([decision 1](#1-measure-before-building)).
8. **One open item is out of date.** It says the occlusion test's "visible"
   bit checks no identity. The kernel does reset a draw's history when its
   generation differs
   (`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-occlusion.comp.hlsl:249`).
9. **One area read sources another could not.** The scheduling area's check
   confirmed quotations from the Lumen, Assassin's Creed Shadows and Destiny
   slide decks, which the presentation area could not fetch. Karis's notes on
   temporal supersampling were listed as fetched by one critic, but the
   judgement that needed them never read them. This page treats them as
   unread.
10. **Three borrowed figures do not carry over to Puck.**
    - Cyberpunk refreshes its distant shadows "every few seconds". The debug
      world's day lasts 24 minutes. A held far shadow map of 480 m radius
      tolerates about 0.4 milliradians of light movement, so at that day
      speed it would need about eleven refreshes a second.
    - Precomputed radiance transfer states that lighting is linear in its
      coefficients. Its treatment of light bouncing between surfaces is
      exact only under distant lighting that is the same everywhere. So
      [decision 7](#7-make-a-lighting-change-cost-what-changed) rests its
      rescaled relight on Puck's own check, not on that paper.
    - Wihlidal's compaction keeps draw order inside one batch and only
      optionally across batches. The visibility plan's sentence about it
      claims more than the source does.
11. **The round restated one plan too strongly.** The streaming plan wants
    the lighting to trace a tier chosen by probe spacing. It asks, as that
    step's first measurement, whether a group's floor alone is good enough
    ([streaming decision 9](streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand)).
    The research areas wrote the floor down as the decision. Decisions 7 and
    8 keep the plan's rule.
12. **One fix that is already built covers less than its name suggests.** An
    unmerged branch makes three changes. It keeps several probe jobs in
    flight. It commits a batch once. And it lets relights made stale by a
    lighting change share the worker with owed traces instead of waiting
    behind them. It was built and never run in a World. Its measurements
    are of the tree it branched from. It still grants lighting work per
    rendered frame (`SdfMeshIrradiance.Grant` at that branch's commit).
    [Decision 4](#4-run-background-gpu-work-without-waiting-a-frame-for-each-job)
    treats these as separate steps.

Evidence for this section: items 2, 8 and 12 rest on code and commit
messages read for this page. Item 3's later figures are measured. Item 10's
Cyberpunk quotation is confirmed, the eleven-a-second figure is reasoned,
and the radiance transfer and Wihlidal claims are partial.

## The decisions

### 1. Measure before building

**The problem.** Most of the gains this round ranks are arithmetic that no
counter can confirm, and every measurement so far comes from the RTX 4070.
Some instruments exist. The console verb `host.frames` splits a loop
iteration into coarse phases. `world.counters render.record` splits the
recording of GPU commands. `world.gpu-timing`, `world.counters gpu`,
`world.residency` and [the benchmark](benchmark.md)'s report per chapter of
its route also exist. The table lists what is missing. Its last column says
which devices each reading is taken on and the first step that needs it, so
that a reading which only ranks a later step does not hold the first stage
open.

| Missing measurement | Why a decision waits for it | Where it is built | Read on, and needed before |
|---|---|---|---|
| Produce split by part: pumping, capture, packing, staging the lighting, exposure, scheduling, preparing packages, recording and flushing | Produce takes about 17 ms and the tick about 8 ms on Vulkan. Recording sums to about 1 ms. So most of produce is unexplained | The job system's work packet, following the pattern of `RenderRecordWork` | The lead machine, both backends. Before stage 2 |
| The tick split by phase, per chapter, and by world instance | Decision 3 shortens the tick by splitting two of its phases. Open items records the debug world's tick as about a third body movement and under a third rule evaluation, with serial causes named. Every world instance also steps in series on the frame thread (`WorldInstanceHost.StepInstances`). No report says which of these dominates | The job system's work packet | The lead machine. Before stage 2 |
| Delivery time and bytes for each session | A world nobody sees still steps and still delivers to its sessions. Decision 2 saves presentation work only, and this row sizes what it leaves | The worlds area ([open items](open-items.md#transfers-federation-and-server)) | The lead machine. Before stage 4 |
| For each attempt to present a frame: whether it was submitted, what happened to it (shown, skipped, dropped or unknown), how long it stood on the display, and which signal said so. The report states the share of attempts whose outcome is unknown | `host.frames` reports the loop's interval, not the display's. Every presenter already reports its latest confirmed present (`IPresentTimingFeedback`). On Direct3D 12 that is the scan-out time. On Vulkan it is the CPU's clock when a wait for the present from two frames back returned (`VulkanFramePresenter.RecordPresentTiming`), and a skipped present is counted but never placed on the timeline. No record is kept per present, so nothing says which frame was shown or dropped | The benchmark's displayed-time stage (stage 8), extending `PresentTimingSample`. A host that cannot report display time says so as a capability, and the column is left out | Every bar device and backend. Before stage 2's frame checks |
| From the earliest input a frame used to the time that frame was displayed, or submitted where no display time exists. Motion the tick integrates and pointer look applied between ticks (`WorldSeatViewState.PresentedLook`) are reported apart | Two decisions turn on latency and nothing measures it: whether the tick ever moves to its own thread (decision 3), and whether waiting for a present slot before rendering costs or saves latency (decision 15) | One joined column of the present record | Every bar device and backend. Before decision 15's admission rule |
| Captures and prepares during produce, counted by what asked for each: a scheduled render, an upload that is owed, another consumer's minimum, or the question "has this view changed?" | Decision 2 saves the captures that nothing reads. That question triggers a prepare too, so it has to be counted | The job system's work packet | The lead machine. Before stage 2 |
| GPU time per benchmark chapter, split into the root view, nested views and background work. Over the same span: the background work's GPU time, the time some job was in flight, the elapsed time, and the credit the budget granted and held | Decision 4 raises the background work's use of its allowance. The only figure today divides GPU time by the time a job was in flight, which is not a share of the allowance, so nobody knows how much of the allowance is used | `world.gpu-timing`, `world.counters gpu` | Every bar device and backend. Before stage 1's headroom rule |
| How long each frame's own GPU work took from submit to completion, per frame, with any background job that ran ahead of it | Pass timings today are means over a window of 32 frames (`ShaderPipelineRenderNode.TimingWindow`), so no per-frame GPU maximum exists. The headroom rule and its check need one | `world.gpu-timing` | Every bar device and backend. Before stage 1's headroom rule |
| One cold bake that records worker activity, the wait on each GPU request, GPU timestamps and the total time together, on a quiet machine with the display composing and an empty store | It shows which of decision 10's two speed fixes is on the critical path | The bake's work packet | The lead machine, both backends. At the close of stage 1 |
| A cold arrival in play: a windowed walk into a place whose meshes are not stored | The cold-bake figure is read paused and in lockstep, where the service hands each job to the GPU before it waits (`SdfGpuWork.Drive`). In play the service only pumps (`SdfGpuWork.Pump`), which is the slow path decision 4 describes. The figure never exercises it | A benchmark chapter run from an empty store | The lead machine, both backends. Before stage 2 |
| The time from saving an edit to seeing it in the running debug world, by phase, for three kinds of edit: a `.puck` document, a shader, and C#. Beside each time, the work it did: groups rebaked, kernels recompiled, projects rebuilt | Decision 10 is judged by this, and it is the wait the owner's ruling puts first. Only scattered readings exist | The edit-to-running procedure's phases ([contributing](../development/contributing.md#edit-to-running), [open items](open-items.md#edit-to-running-hot-reload-and-boot)) | The lead machine. Before stage 1 |
| Shadow queries the relight actually issued, and pairs of hit and light it actually tested, separate from the scheduler's predicted cost. Beside them, how many distinct pairs of surface and light those were, and the most work in one dispatch | Decision 7 sizes its stores by executed work. Traffic alone does not size a store: one pair asked a thousand times needs one entry, and a thousand pairs asked once need a thousand | `world.counters gpu` | The lead machine. Before stage 3's shared surface lighting |
| For each probe level, at a walk, a run and in flight: probe cells entering the grid, and probes traced and relit a second | The lighting's lag has only been read standing still. A moving camera brings new cells in, and whether tracing keeps up decides whether the lag check means anything in play | `world.counters gpu` | The lead machine. Before stage 1's lighting check |
| Triangles drawn per level, and per tier relative to the tier that was asked for. Bytes held by each level that no pass selected | Decision 8's reasoning about unused detail has no counter behind it | `world.counters gpu`, `world.residency` | The lead machine. Before stage 3's chain |
| Triangles submitted, culled and rasterized in each raster pass | Indexed draws claim to cut vertex work, and no counter shows vertex work | `world.counters gpu` | Every bar device. Before the indexed-draw experiment |
| How much of each cascade's grid of receiver cells is occupied | It decides whether shadow storage could be allocated on demand, and it sizes decision 11's caster culling. Only the far cascade has a receiver mark today, so the first stage builds a count-only mark for the other two | The virtual geometry plan's shadow measurement (D8) | The lead machine. Before stage 2's caster culling |
| For each pass, one node per residency and one per view of it, naming the residency each view reads | Decisions 2, 11 and 14 depend on it. Sessions and residencies do not map one to one in either direction. The boot world's sessions are views of its one residency, and every seat and window presented in one other world share that world's one residency (`WorldFramePresenter.AttachWindow`) | The visibility plan's owed measurements | The lead machine. Before stage 2 |
| The capture and GPU readings again, with one window onto a world that gives the viewer less than a full replica | Every world of the debug world admits everyone at full disclosure (`disclosure: Replica` in `worlds/debug/debug.puck`), so every window joins a shared residency. A restricted viewer gets a residency of its own (`WorldScreenBinder.CreateSessionResidency`). Without this row every sharing claim is measured only where sharing is cheapest | A scratch destination admitted below a replica, in the same process | The lead machine. Before stage 3 |
| What an empty indirect draw command costs on each bar device | It decides whether compacting the command list is enough on its own | Visibility's compaction step (V1a) | Every bar device. Before compaction |
| One cascade pass split into walking the commands, vertex work, pixel fill and clears | It decides which of three fixes pays | A GPU capture in the lead's measurement window | Every bar device. Before stage 2's caster culling |
| What re-uploads about 2,600 draw records a frame when nothing is moving | It may be a defect that is cheaper to fix than any step that claims the same saving | Visibility | The lead machine. Before the draw record is split |
| Garbage collections and bytes allocated per iteration, by source | One run had 47 collections that paused the process for 674 ms in 600 iterations | The job system's work packet | The lead machine. Before stage 2 |
| The mesh audit over every stored mesh of the world, at the tier each group is stored at, on each backend | Decision 10's contour rule reaches every bake, and the audit that would judge it does not cover the world today. A store audit over a cold store read 238 of 6,544 groups, and the CPU audit disagrees with the device below cells of about 8 mm ([bake detail](open-items.md#bake-detail)) | `puck mesh audit` | The lead machine, both backends. Before stage 1's contour rule |
| Any pass at all on the RTX 2060 and the Steam Deck, on every backend the device runs: Direct3D 12 and Vulkan on the two RTX cards, Vulkan on the Deck | Four plans wait on it: visibility's device measurement (M2), the author-shader matrix, illumination's first step and transmission's budgets | One GPU session that serves all four plans | The RTX 2060 and the Steam Deck |
| For each bar device: the slowest frame of each chapter against the device's bar, split into the tick, produce, the root view, the cascades, nested views and background work | It says how far each device is from its bar and in which part, so that each later step can state its gain against that distance | The same device session | Every bar device and backend. Before stage 1 |

**What we propose.** Take the measurements in the table first, as one stage
that changes no behaviour (option 1b). Where a measurement serves only one
candidate, take it when that candidate starts (option 1c). There are five of
those: how many probe hits share one surface, the variants of the per-pixel
probe lookup, how many passes a lighting drain takes, the cost of the
exposure meter when it is compiled out, and a per-tier audit of each foliage
prototype.

The device rows produce one table per bar device: the distance from its bar
in each chapter, by part. From then on each step states its claimed gain
per device against that table. Where the gains planned for a device do not
add up to its distance, the order is cut again for that device, and what
still remains goes to the owner
([the owner's calls](#the-owners-calls)).

**Why.** A fix ranked by arithmetic can spend weeks on a cost that turns out
to be small. The measurements are live counters and benchmark reports read
in real time, which is how the owner's rulings ask for evidence to be taken.
Nothing is recorded as a baseline.

**What it costs or risks.** Measuring first delays fixes the owner can
already see. Three things keep that delay small. The fixes in decisions 5
and 6 that rest on the code alone do not wait. Only work whose rank depends
on a number does. And the stage closes device by device: the lead machine's
readings are enough for stages 1 and 2 to start, so those do not wait for
the RTX 2060 or the Steam Deck.

There is a second risk: a stage that only explains where cost goes proves
nothing about whether a later change paid off. So from stage 1 on, every
check names the gain its change claims and what result would show the claim
false ([the build order](#the-build-order)).

A third risk is that the ranking goes stale. Stage 1 changes the bake's
cost, so the cold-bake run that ranks the speed fixes is taken at the close
of stage 1, not before it.

**What we still have to find out.** Everything in the table.

**The option we did not take.** Building the ranked candidates on arithmetic
alone (option 1a) is the fastest start, and it is how a team ends up
optimizing the wrong thing.

**Evidence.** Code read for this page: the Vulkan presenter's timing
behaviour, the presenters' shared timing interface, the window of 32 frames
behind a pass timing, the serial step of world instances, the debug world's
admission at full disclosure, and the routed residency shared by an
endpoint's seats and windows. From the reviews, checked by their own
readers and not reopened here: that the cold-bake figure is read paused and
in lockstep, and the store audit's 238 of 6,544 groups. Measured: produce at
about 17 ms and the tick at about 8 ms on Vulkan, and the 47 collections
with 674 ms of pauses in 600 iterations.

**What it rests on.** "Hunt real problems with every instrument", "Evidence
is taken as a player takes it" and the ruling on waits, which puts the time
from a change to seeing it first
([AGENTS.md](../../AGENTS.md#puck-is-pre-release)).

### 2. Capture and prepare only what a frame shows

**The problem.** The frame thread does work for worlds, views and bodies
that no pixel of the frame will show. The cause is the order of operations.

The host loop steps the simulation and then calls produce on the same thread
(`src/Puck.Launcher/HostLoop.cs:169-184`). Produce starts every render
package's frame before it decides which views to render:
`PackagesBeginFrame` runs ahead of `RenderGraphScheduler.Schedule`
(`src/Puck.Shaders/Graph/RenderGraphRuntime.cs:1253-1267`). The package that
draws worlds then prepares every residency it holds, whether or not anything
shows it (`src/Puck.SdfVm/SdfWorldPasses.cs:480-484`):

```csharp
        foreach (var residency in m_residencies.Keys) {
            if (!residency.IsReleased) {
                _ = residency.Prepare(context: in context);
            }
        }
```

Preparing a residency captures its frame. It reads the render poses, the
mirror of simulation state, the field probes that place characters' feet
(their effectors) and the camera's clearance march, and it packs the poses
for the GPU.

The scheduler asks for the same work a second time. Before it schedules,
the runtime asks every package whether each view's last render still stands
(`RenderGraphRuntime.WithUnchanged`). The residency answers by preparing
(`src/Puck.SdfVm/SdfWorldResidency.cs:644-646`):

```csharp
    public bool IsUnchanged(in FrameContext context, int view) {
        if (
            !Prepare(context: in context) ||
```

It then compares a hash of the packed tables with the hash the view last
rendered at (`SdfWorldTables.ViewSignature`). That is expensive. It is also
safe: a change the hash covers cannot be missed, because the hash is taken
after the capture and the pack.

Two things in today's order are right and have to be kept.

- **The root world's capture produces the cameras and the footprints.** A
  footprint is how much of the frame a view covers. The root capture places
  the cameras, and then every view's footprint is completed, at every depth
  of nesting, before the schedule runs (`WorldViewGraphHost.PublishPanes`,
  which ends by calling `CompleteFootprints`). So a camera turn that reveals
  a window, and a second window inside the first, schedules both in the same
  frame today. A view whose camera is not yet known keeps its footprint, so
  it is served too much and never missed.
- **A prepare also does upkeep that has nothing to do with drawing.** It
  polls finished uploads, answers requests to give memory back, and applies
  a shader reload (`SdfWorldResidency.Prepare` and the step it runs once a
  frame). A residency that simply stopped being prepared would stop doing
  these.

Three more costs come from the same shape:

- Each world that is arriving renders a hidden window at
  `WorldPortalVisibility.ArrivalShare`, because only a scheduled render
  moves a residency's data to the GPU.
- Every session of the boot world is filmed into the frame whether or not
  its instance renders (`WorldScreenBinder.FilmHomeViews`).
- Packing poses looks up the driver of each authored swing by comparing
  strings, on every pack (`src/Puck.World.Client/WorldGaitDrivers.cs:610-626`).

**What we propose.** Six steps, in this order.

1. **Get the breakdown** of produce from decision 1.
2. **Capture only the residencies a frame needs** (option 2b). This has
   four parts, and they land together.
   - *The order.* The root world's capture stays first, because nothing
     else can supply the cameras and the footprints. The footprints are
     then complete at every depth, as they are today. After that, a
     residency is captured and packed only if something needs it this
     frame: a view with a footprint, an upload that is owed, or another
     consumer's minimum. The schedule follows.
   - *Upkeep for every residency, every frame.* One small step runs for
     every residency whether or not it is captured: it polls uploads,
     answers memory trims and retirements, and applies a pending shader
     reload. It reads no world state and its cost does not grow with the
     world.
   - *Minimums for unseen worlds, in the same piece of work.* The hidden
     arrival render exists because only a scheduled render moves a
     residency's data to the GPU. Deleting it with nothing in its place
     would leave a cold destination unable to make progress. So what each
     consumer is owed is registered and served in this step: a predicted
     arrival's first image and its lighting, a screen out of view whose
     glow lights a visible room, picking, and the lighting trace
     ([decision 14](#14-treat-a-nested-view-as-the-same-view-at-another-size)).
     That work runs under the shared budget and never drives the frame.
     Stage 4 later divides the optional work above these minimums.
   - *State kept by view, not by position.* Exposures, the occlusion test's
     previous visible set and the temporal history are keyed by the view's
     identity. Skipping a view then shifts nobody else's state
     ([visibility V5](visibility.md#v5-multi-view-and-shadow-work)).

   Freshness keeps its one judge through this step. For every residency
   that is captured, the packed hash still decides whether a view's last
   render stands. The existing rule that a view may stand only while it is
   bound to the same resolved view also stays (`SdfWorldPasses.Refresh`). A
   viewer who loses the right to see a world is moved to another residency,
   and that rule is what stops the old picture standing.
3. **Only if the breakdown calls for it, answer "has this changed?" without
   capturing** (the second half of option 2b). After step 2, a residency
   that is shown still captures and packs every frame to learn that nothing
   changed. If the breakdown names that as a cost, a cheap check can let a
   view stand from change stamps: counters that each writer of an input
   moves when it changes that input. The packed hash stays the judge
   whenever a capture does run. This step is built with three guards,
   because its failure is silent:
   - one constructor takes every stamp, so a new input cannot be added
     without one;
   - an audit switch packs every view the cheap check let stand, compares
     the hash, and counts disagreements;
   - the change gets an adversarial review, because a guard every writer
     must remember is a class of defect that reading the change is the only
     check for ([hazards](../development/hazards.md#silent-invariants)).
4. **Cut the largest named parts at their cause** (option 2c), in the order
   the breakdown gives: CPU queries of the distance field, draw records that
   are re-uploaded, and motion that is interpreted every frame
   ([decision 12](#12-keep-motion-and-placement-as-authored-programs-at-zone-scale)).
   Field queries are about a third of the meadow's frame. The field's
   gradient is the first candidate among them. It takes six samples for
   each query (`SdfFieldEvaluator.TryFieldGradient`), and open items already
   holds a version that takes four. One walk that carries the derivatives
   along is weighed against it by measurement, because it would be a second
   walker of the field.
5. **Run what remains in parallel on the one thread pool** (option 2d).
   Packing poses is 0.9 ms of the 3.4 ms it takes to begin a frame. This
   step waits for the job system's bounded waits and its pool (C0 and J1).
6. **Stop allocating memory every frame**, on the frame thread and the
   background threads, at the sources a trace names (option 2e). This runs
   beside all the other steps and needs no pool. One iteration allocated
   68 MB, and the worst frames carried garbage-collection pauses of 20 to
   28 ms, which is longer than a whole frame. That causes hitches at any
   average frame rate.

**Why.** Capture cost today grows with the number of residencies held, not
with what is on screen. Destiny's renderer extracts only what is visible and
creates job chains only for views in the frame. The streaming plan already
decided that a residency is staged by what demands it, not by a render
([streaming decision 4](streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render)),
and its five parts are this decision's design. The change removes three
things: capture that grows with residencies, the hidden render per arriving
world, and the case of a world behind a rarely updated view that never
finishes arriving.

Step 2 gets most of that saving without a second answer to "is this view
fresh?". The residencies that cost the most for nothing are the ones no
view shows, and those need no freshness answer at all.

**What it costs or risks.**

- The root capture cannot move. An earlier draft of this page said the
  cameras and the footprints could be read "from state that needs no
  capture" and scheduled a correction pass for a window revealed
  mid-frame. The code does not support the first, and it does not need the
  second: footprints are already complete before the schedule. That draft's
  check also accepted a window showing its fallback image for one frame,
  which today's order never does.
- A residency that is not captured must still do its upkeep, which is why
  the upkeep step is separate from the capture.
- Removing the hidden arrival render before the minimums exist would strand
  a cold destination, which is why they land together.
- State indexed by position shifts when a view is skipped, which is why the
  re-keying is part of the step.
- The cheap check of step 3 is a second answer to a question the packed
  hash answers today, and two answers can disagree. A stamp that a writer
  forgets to move shows as a picture that should have redrawn and did not.
  No counter notices, because a view that wrongly stands also lowers the
  capture count the step is judged by. This is why step 3 is optional, why
  it comes after step 2, and why it carries an audit.
- Switching the garbage collector's mode would hide the allocation problem
  without fixing its cause, so we do not do it.
- This order has been read by one model family only. The second review,
  from another family, found the missing minimums but did not examine the
  capture order itself.

**What we still have to find out.** The breakdown may name parts that none
of these steps covers. The order follows the breakdown. Whether capturing a
shown but unchanged residency is a cost large enough to justify step 3.

**The option we did not take.** Recording several views' commands in
parallel now (option 2a, the job system plan's R1) would save about half a
millisecond, because recording all views in series takes only about 1 ms in
the debug world. The job system plan's own admission rule keeps it on hold.

**Evidence.** Code read for this page: the loop order, the begin-frame call
ahead of the schedule, the prepare of every residency, the freshness
question that prepares and then compares the packed hash, the completion of
footprints inside the root's capture, the upkeep inside a prepare, the
arrival window, the filming of home sessions and the string lookup. From the
attack's readers, not reopened here: that a view with an unknown camera
keeps its footprint, and that a viewer who loses an entitlement is routed
to another residency each frame (`WorldSessionRoute.Settle`). From the
scheduling area's map: what a prepare captures. Measured: recording at about
1 ms, field queries at a third of the meadow's frame, pose packing at 0.9 ms
of 3.4 ms, 68 MB allocated in one iteration and pauses of 20 to 28 ms.
Outside sources: Tatarchuk on Destiny (slides 57 and 73), confirmed.

**What it rests on.** Work proportional to change, and the streaming plan's
decision 4. The cuts to field queries stay with the visibility plan's steps
(V2a and V2b). They build no second cache of field answers beside
`WorldFieldAnswer`. The outcome we hold ourselves to is about presentation:
a view that nothing reads costs the frame thread only its upkeep, and a
world nobody sees costs only its reduced update rate, its minimums and the
uploads it owes. That world's simulation step and its deliveries to
sessions are not presentation work and this decision does not reduce them.
Decision 1 measures them, and decision 3 names stepping world instances in
parallel as a candidate.

### 3. Keep the tick ahead of the frame, and make it shorter

**The problem.** A frame that also steps the simulation carries a whole tick
before it starts to build the picture. On the round's measurements that is
about 8 ms of tick in front of about 17 ms of produce. The question is
whether the tick should leave the frame thread.

**What we propose.** Keep the tick where it is, ahead of produce, and
shorten it (option 3b). The tick splits its work into partitions that run on
the thread pool. The frame thread waits for them inside the step. Work that
runs at a reduced rate is kept off the frames that tick.

The steps come in the order [the job system plan](engine-job-system.md#6-build-order)
fixes, and that order is a real dependency:

1. Bound the control plane's waits (C0). The control plane is the console,
   the tool connection and input.
2. Build the pool, with a fork that returns an operation its forking thread
   owns (J1).
3. Then split the crossing scan and the rigid bodies into partitions (T3,
   T2).
4. Fix the tick's serial costs at their cause, in the order the tick's
   breakdown gives.

Only the measurements and the snapshot partitions that are already built
come before the pool.

Step 4 is there because the two partitions of step 3 may not be where the
debug world's tick spends its time. The crossing scan is split 32 seats to
a partition, and the debug world has a handful of seats, so that split
saves nothing there. The rigid bodies' share of the tick has never been
measured. One trace of a warm debug world shows about a third of the tick
in moving bodies and under a third in evaluating rules, each with a serial
cause that [open items](open-items.md#other-frame-time-findings) names: a
bounds cache that misses for every box of a moving body
(`BodySupportMemo.Query`, `SdfBoundsCache`), and a scan of every body's
facts on every rule pass (`WorldRuleHost.DeriveFacts`). Decision 1 adds the
tick's breakdown by phase, and that breakdown ranks these fixes against
the partitions.

One more candidate is ranked by the same measurement. Every world instance
steps in series on the frame thread (`WorldInstanceHost.StepInstances`),
and the job system plan forks nothing across instances. Stepping instances
in parallel is weighed once decision 1 shows what the instances cost.

**Why.** The alternatives make the frame shorter by showing the player an
older simulation state. The register rules that out: "A lower tier, a
coarser shadow snap, reduced contact detail or a frame of latency is never
the fix for a missed bar"
([the pipeline foundation](../decisions/rendering.md#the-pipeline-foundation)).
The job system plan withdrew its own pipelined simulation worker for the
same reason: "no input latency is added and no tick runs under a produce".
Puck's purpose weighs feel as heavily as looks.

The order matters because of how a join behaves when it runs late. With the
pool in place, a join that passes its wait budget keeps the logical tick
open across loop iterations and lets the control plane run. Without it, new
partitions would go through today's join, which waits inside one call. Input
and the console would then be held for as long as the slowest partition.

The bounded waits and the single pool pay for themselves whatever the
partitions turn out to save, because every other piece of CPU work this
page moves off the frame thread runs on that pool.

**What it costs or risks.** Two parts of the tick stay serial until their
transaction is redesigned. The job system plan calls them kit bodies and
rule gates. So partitions shorten the tick but do not remove it, and in the
debug world they may shorten it very little.

The strongest case against this decision is that it leaves time on the
table today. While the frame is limited by the CPU on a fixed-refresh
display, a late frame already costs a whole refresh, so a tick on its own
thread would lose nothing there. That holds only while the bar is missed,
and the proper fix is to stop missing it.

**What we still have to find out.** Where the tick's time goes, by phase.
Whether the tick and produce each fit a frame on their own while their sum
does not, once decision 2 has landed. If so, moving the tick to its own
thread comes back as a question. It needs a measured cost for the hand-off,
which is decision 1's reading of the time from input to display. It would
then be the owner's call, because it reverses a recorded ruling about what
the engine does.

**Options we did not take.**

- *Leave it serial* (option 3a). It fixes nothing: the frame stays the sum
  of tick and produce.
- *A tick on its own thread that publishes a snapshot, with the frame thread
  only producing* (option 3c). The frame interval would fall from the sum to
  the longer part, from about 25 ms to about 17 ms. But the frame would show
  the tick before the one the player's input reached, up to a whole
  iteration later. No source we read measures that hand-off's latency.
- *The tick overlapping produce, as Destiny pipelines its frames* (option
  3d). It has the same latency. Destiny's slides are confirmed for how it
  extracts, not for latency.

**Evidence.** Code read for this page: the loop order (decision 2). From the
job system plan: its contract, and that the snapshot partitions are built.
Reasoned: the fall from 25 ms to 17 ms. Outside sources: Tatarchuk on
Destiny, confirmed for extraction only.

**What it rests on.** The register's ruling against a frame of latency, the
job system plan's withdrawal of the pipelined worker, and the weight Puck's
purpose puts on feel. One related piece of work is still built: the runtime
plan's presentation view, which makes produce read no server state
([runtime plan](runtime-and-delivery.md#the-presentation-view)).

### 4. Run background GPU work without waiting a frame for each job

**The problem.** After a lighting change the indirect light takes far too
long to catch up, and the cold bake may be slow for the same reason. The GPU
is not short of time. The work waits on round trips.

Background GPU work is everything the GPU does that is not drawing the
frame: relighting probes, tracing their rays, sampling the distance field
for the bake, and uploading finished meshes. One service runs it
(`SdfGpuWork`), under one budget shared by every residency the application
presents (`SdfGpuWorkBudget`). The budget is a share of GPU time, counted against the
wall clock (`SdfGpuWork.GpuShare`). The service has a fixed number of job
slots (`SdfGpuWork.SlotCount`).

Three things in the lighting client throttle it. It admits one query at a
time (`src/Puck.SdfVm/SdfMeshProbeGpuWork.cs:249`):

```csharp
            if (m_inFlight || !tables.BindsBackgroundWork) { return false; }
```

Its coordinator blocks on each answer (`SdfMeshProbeGpuWork.Submit`), and
every relight job is followed by a separate job that commits it. And the
relight's allowance is credited per rendered frame, capped at two frames'
worth (`SdfMeshIrradiance.Grant`). A game presenting 30 frames a second is
therefore granted half the lighting work per second of one presenting 60.

One windowed run measured the result. Each probe job took about 25 ms to go
out and come back. The service's report gave an occupancy of 0.05. That
figure is GPU time divided by the time some job was in flight
(`SdfGpuWork.Describe`). It says the GPU was working for a twentieth of the
time a job was out. It is not a share of the allowance, and nothing yet
reports how much of the allowance was used.

After a switch to night in that run, the finest probe level's lighting was
up to 44 seconds old. The run shows two more causes beside the round trips,
and it does not divide the 44 seconds between them. Relights that the
change made stale waited behind traces that were still owed: a second
switch, with no traces owed, relit the finest level in about 90 ticks,
which is 3 seconds. And the lighting's version, the counter that marks a
lighting change, moved about 1,380 ticks after the day was set. That is 46
seconds before the age even starts to count. The run was on the tree the
unmerged lighting branch started from, and the way to reproduce it is in
[the lighting work packet](open-items.md#lighting-and-gi).

A group's bake has the same shape. It samples up to five lattices that
narrow in on the surface, then its final slabs, then up to eleven rounds
that search for the exact points where the surface crosses a lattice edge,
then gradients and materials. Each step reads its result back to the CPU. A
quiet cold bake has medians of 60.6 s on Direct3D 12 and 64.7 s on Vulkan,
with only about 4 s of GPU work in a busy span of 68 s.

Two more things are sized by fixed constants on every device.

- **Bake jobs are too long for a frame.** The service's contract says a
  client cuts each job to the frame's allowance at its measured rate,
  because a job runs whole before the next frame's work starts. Only the
  probe client does. Bake batches are cut by a count of samples
  (`SdfMeshPathBake.BatchTaps`) and charged at a fixed cost per sample
  (`SdfMeshPathBake.NanosecondsPerTap`). A full batch is 8 million samples,
  about 32 ms at that constant, against an allowance of 4.2 ms. So a bake
  during play already submits jobs several frames long, on the RTX 4070
  itself.
- **Uploads of finished meshes run at one byte rate**
  (`SdfGpuWork.UploadBytesPerSecond`), and both the allowance for one job
  and that rate assume a 60 Hz display (`SdfGpuWork.FrameShareSeconds`).

**What we propose.** Six steps, in this order.

1. **Make the budget follow the GPU time the frame actually leaves free**
   (the first half of option 4g). This comes before anything raises the
   service's use of its share. The engine already measures the frame's GPU
   load for another purpose: dynamic resolution reads the views' GPU time
   against the display's period, with a dead band of a tenth either side
   (`IWorldFrameLoadSource`, `WorldDynamicResolution`). The headroom rule is
   built on that reading and not beside it.
   - *One reading.* The load source covers the root views today. It is
     extended to nested views and background work, so one figure says what
     the frame's own passes took.
   - *One period.* The period is the display's (`FrameContext.DisplayHertz`),
     not a constant 60 Hz.
   - *The share.* The background share is what the root and nested views
     leave of the period. It has a lower limit, which keeps lighting from
     starving, and an upper limit, which is today's `SdfGpuWork.GpuShare`.
   - *Jobs follow the live share.* Every client cuts each job to the live
     allowance for one frame. A job already in the GPU's queue cannot be
     recalled, so lowering the next grant does not protect the frame behind
     it. Each job is therefore admitted against the frame's deadline: its
     own priced length, the jobs already queued and the credit already
     banked, with a margin. The budget can bank 25 ms of GPU credit today
     (`SdfGpuWorkBudget.MostBankSeconds` times the share), which is more
     than a frame. Banked credit is spent only as jobs that each fit one
     frame's allowance.
   - *The loop, written down.* Its input is decision 1's per-frame GPU
     span. The share moves by a bounded step, inside the same dead band
     dynamic resolution uses, and a change to a nested view's rate needs
     the reading to hold for a stated time before it reverses. A loop
     without these can swing between two states.
   - *Two controllers, one order.* Dynamic resolution is off in every
     quality preset today, and then only the background share moves. With
     it on, both respond to one reading. Optional background work gives
     way first. The render grid moves only when the frame's own passes
     exceed the period on their own.
   - *What gives way under overload, and how far.* Optional background
     work first. Then the update rate of nested views, but never below the
     rate tier its author set, which the register calls "a floor held by
     presentation time". The minimums are last and are always served. They
     are stated as work per second in authored units: the work that holds
     the authored lighting age. When the frame's own passes already fill
     the period, serving the minimum costs frame time. That cost is bounded
     by the size of one job, and it is the one exception to the rule that
     overload costs latency and never frame time. Frames that miss their
     deadline behind a background job and lighting that is overdue are
     counted apart.
   - *What measuring costs.* GPU timestamps are recorded only while a
     reader asks, because they cost a query pool and a readback for each
     frame in flight. The headroom rule makes that cost permanent, and it
     is reported. A device that cannot time its passes keeps today's fixed
     share and says so.

   A lockstep drain ignores the measured share, so its result stays a
   function of its inputs.
2. **Cut bake jobs to the allowance.** Open items already holds this, with
   one failed attempt: cutting by the bake's single measured rate shrank
   every batch to its costliest group's rate and cut the groups in flight to
   a third. The rate that works is one per kind of lattice, or one that
   follows the mean. The cold bake is measured again with it.
3. **Let several probe jobs run at once, in three parts** (option 4b).
   - Several jobs in flight, with one commit per batch.
   - Relights that a lighting change made stale become due at once and
     share the worker with owed traces.
   - Then the grant per second, last. The relight's separate credit per
     frame (`SdfMeshIrradiance.Grant`) is deleted, and the relight draws on
     the one budget's wall-clock credit. The authored allowance stays in
     work per second, so it means the same on every device. It is turned
     into GPU time through a measured rate (`SdfGpuWorkRate`) only to
     charge the budget. The estimate of an upload's GPU time becomes a
     measured rate of the same kind. The cap on bytes staged a second is a
     separate limit and stays.

   Each increase in throughput is measured beside the root frame's time
   and the lighting's age, in the same measurement run, on both backends.
   The delay before the lighting's version moves is reported on its own,
   because the age cannot see it.
4. **Move the work list onto the GPU** (option 4c). The probe client
   already chains three things on the device: a kernel that fills its
   cache, a compaction and an indirect dispatch. This step adds the rest.
   Probe state lives on the device. A kernel selects what to do next by a
   deterministic key. The CPU reads only counters.

   The cap on one dispatch has to be a cap on the work actually done. A
   count of probes does not bound it, because a probe's cost is known only
   after its rays land. The scheduler charges a point light to a probe when
   the light's range reaches the probe's origin
   (`SdfMeshIrradiance.RelightRays`). The kernel tests each hit's own
   distance to the light. A probe outside every light's range can still see
   a wall that many lights reach, so its predicted cost is zero and its
   real cost is large. The kernel therefore counts the tests it makes
   against the cap, and a probe that would pass the cap stops and resumes
   in the next dispatch.

   This step lands before the lighting's CPU loops move to the thread pool,
   because both rewrite the same code (`SdfMeshIrradiance`).
5. **Hand background jobs to the GPU queue before the frame's own passes
   are recorded** (option 4e). Two parts of this exist. Each residency's
   background work is already pumped before any residency prepares. And a
   frame the graph withholds already hands each job to the queue before it
   waits (`SdfGpuWork.Drive`). What is new is the same hand-off in an
   ordinary presented frame (`SdfGpuWork.Pump`). A wait then names the
   exact queue position it needs, and a job is answered at the next step
   and not the next frame. It costs one more submit a frame, and it is kept
   only if a measurement shows the answer does arrive a step earlier. It
   lands after the job system's rework of waits.
6. **Price every consumer in one unit** (the rest of option 4g): nested
   views, uploads, lighting and bake. The scheduler already honours a
   ceiling on passes times pixels that no caller sets
   (`RenderGraphFrame.PassPixelBudget`), and that is where nested views
   join. This is a design round, held until the share is actually in use.

Doing the bake's sampling in one round trip per stage (option 4d) stays a
candidate. Its rank waits for the measurement of what limits the cold bake
([decision 10](#10-make-a-bake-cost-what-changed-and-close-the-contour-defects)).

**Why.** Raising the share or the slot count would do nothing while the
work waits on round trips. Shipped systems hold update cost fixed and let
lighting latency vary: Lumen describes "fixed update cost, variable
lighting latency", and Frostbite's probe system holds a fixed budget of
100,000 rays and about 6,000 probes a frame.

The headroom rule comes first for a reason that is easy to miss. On the
round's measurements the root frame uses about 12.6 ms of GPU time before
any nested view. The background allowance at 60 Hz is 4.2 ms. If the
background work used all of it, the two would come to 16.8 ms against a
16.7 ms frame. The 4.2 ms is an allowance and not a measurement, so this is
a scenario and not a reading. It is the scenario the first fix that makes
the lighting worker effective would create.

**What it costs or risks.**

- An average budget cannot protect one frame. A long job already queued
  delays the frame behind it whatever the next grant is. This is why every
  job is cut to a frame's allowance and admitted against the deadline.
- Several jobs in flight change the order in which bounces of light are
  computed inside a batch. Every probe of a batch therefore reads the
  bounce from before the batch, and a drain publishes at stated points, so
  the size of a batch cannot change what a drain converges to.
- A per-second grant needs authored units and a bounded catch-up. A lockstep
  drain must give the same result whatever the play rate.
- A recorded chain cannot be cut short, so its cap must be an input to the
  kernel. One dispatch of 4.4 million rays once ran past the device's
  timeout.
- A second submit to the GPU each frame is the price of the early hand-off.
- The work list on the GPU and the early hand-off both sit in classes of
  defect that only review catches: a GPU job whose inputs are freed while
  it runs, and a GPU read at a position another GPU job wrote
  ([hazards](../development/hazards.md#silent-invariants)). Each gets an
  invariant row and an adversarial review. One Vulkan device loss in this
  area is still unexplained.
- Two controllers on one reading can fight, which is why their order is
  stated.
- Standing GPU timestamps cost a little every frame.
- The strongest case against the order is the scenario above: today's low
  use limits nothing that a working worker does tomorrow, and the allowance
  can overrun the frame.

**What we still have to find out.**

- How much of its allowance the background work uses today.
- How the 44 seconds divides between its three causes.
- The batch half of step 3 exists on an unmerged branch and has never run in
  a World. By that branch's own account, one admission door makes a query
  that replaces shared probe state run alone, and a batch's last job commits
  only the rows its batch wrote. The per-second grant is not built: the
  branch grants per frame, as the main tree does.
- Schedulers that keep their work list on the GPU have precedent. What we
  found no source for is one that also drains to the same result whatever
  its history. That half is Puck's own to prove.
- Whether the bake's round trips are on its critical path.

**Options we did not take, or hold for later.**

- *Raise the share or the slot count* (option 4a). It fixes nothing, because
  the round trips remain.
- *The bake's sampling in one round trip per stage* (option 4d): the root
  search and the gradients move into the kernel, and the narrowing lattices
  chain on the device. A group's roughly twenty round trips would fall to
  about four. A chained job needs a cap on its samples for the same reason
  the relight needs one on its tests. The risk is two implementations of
  one rule. The rule that finds a crossing (`SdfSampledLattice`'s root
  search, with its bracket, tolerance, round limit and step) is written in
  C# today. Writing it again by hand in shader code would make two copies,
  whoever is asked to keep them equal. Its first step is therefore one
  source that both evaluators are generated from or read. No source
  measures a root search on the GPU. Dreams ran a hierarchical GPU
  evaluator on a console, which is the nearest precedent.
- *A second GPU queue for background compute* (option 4f). It would stop
  long jobs delaying the frame behind them. The only figure is a Khronos
  sample's 21.8 ms against 22.9 ms. That sample is about tile-based mobile
  GPUs and shows two workloads overlapping. It does not show that a queue
  can be pre-empted on the bar devices. It stays an experiment with a stop
  rule, and steps 1 and 2 have to hold without it.

**Evidence.** Code read for this page: the one-at-a-time admission, the
blocking coordinator, the per-frame grant, the slot count and the shared
budget, how the service's report computes its occupancy, the bake's batch
size and fixed cost per sample, the allowance and bank constants, the
dynamic resolution controller and its dead band, the scheduler's unset
pass-pixel ceiling, the relight's predicted charge against what the kernel
tests, and the pump ahead of the prepares. Also read: the commit message
that recorded the 25 ms round trip, the 0.05 occupancy, the 44 s age, the
second switch and the delay before the version moved, and the bake medians.
From the area's map: the shape of a group's bake, the 4.4 million-ray
timeout, and that the pump after an upload runs inside the frame's open
batch, whose completion signals are shared with the batch's owner.
Measured: about 4 s of GPU work in 68 s, and the 12.6 ms root frame, whose
reading records no window size. Reasoned: twenty round trips falling to
four, 32 ms for a full bake batch, and the 16.8 ms scenario. Outside
sources: Apers on Frostbite and Wright on Lumen's fixed update cost, both
confirmed. Dreams' GPU evaluator and the Khronos sample's timings are
unverified.

**What it rests on.** Work proportional to change, the illumination plan's
rule that overload degrades latency and never frame time, and one
implementation. `SdfGpuWork` stays the one service and `SdfGpuWorkBudget`
the one budget. The frame's load has one reading (`IWorldFrameLoadSource`).
The job system's `Lighting` class supplies the policy. Waits stay on the
host's primitives through `HostWaits`. The relight and trace allowances and
the lighting age are authored (`render.indirect`'s `budget`), and their
unit becomes work per second.

### 5. Convert scene light to display values exactly once

**The problem.** Tones are wrong across the whole picture, and HDR output is
wrong in a second way. The cause is a missing conversion, not a setting.

Two kinds of pixel value are in play. *Scene light* is linear: twice the
value is twice the light. The *working image* is what the rest of the frame
and the display expect. It is encoded on the sRGB curve, with 1 at SDR
white ([the shader reference](../reference/shaders.md)). The step that turns
one into the other does not exist on any path a world's view takes to the
display.

- A view's final pass multiplies linear luminance by the exposure and stores
  the result. The root graph's `place` pass, which positions each view in
  the window, adds at most a filmic tone curve. Its comment says "no gamma
  encode follows it"
  (`src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl:20-21`).
- Authored colours have the same split. A hex colour enters shading as
  linear reflectance: "the render path is gamma-naive end to end"
  (`src/Puck.Maths/HexColor.cs:11-12`). Colours that are keyed over time
  decode the same hex before they blend (`WorldKeyResolver`).

The display encode then treats the stored linear value as already encoded.
For an SDR display it writes the value as it is. For an HDR display it
decodes it with the sRGB curve
(`src/Puck.Shaders/Assets/Shaders/Runtime/display-encode.frag.hlsl:55-59`):

```hlsl
    if (encode.colorSpace == DisplayScRgb) {
        return float4(((puckSrgbToLinearExtended(color) * encode.whiteScale) + dither), 1.0);
    }

    float3 sdr = saturate(color + dither);
```

So linear light is shown as if it were encoded on an SDR display, and it is
decoded a second time on an HDR one.

A worked number shows the size of the error. The exposure meter aims the
average of a scene at 12.5 / (100 × 1.2) = 0.104 of white. Written straight
to the display as a code value, 0.104 comes out as about 1% of white in
actual light. With the encode it would be written as code 0.36 and come out
as the intended 10.4%. The debug world compensates by hand. It authors
`compensation: 2` with the note "Two stops over the meter keep the world's
pale look" (`worlds/debug/hall.puck:1513-1530`). That corrects one point on
the curve and leaves the rest wrong. The terrain pass already expects the
standard conversion: its grey card must read 118/255, the sRGB code for 18%
grey ([terrain pass, step 1](terrain-pass.md#order-of-work)).

The fix cannot go in one pass, because a view reaches the display by several
paths and `place` is on only some of them:

- A single full-screen view with no tonemap, sharpen, bloom or matte is not
  placed at all. The root graph composes no pass for it
  (`WorldRootGraph.PlacesViews`), and the view's image stands for the whole
  world (`src/Puck.World.Client/WorldViewGraphHost.cs:831-833`).
- A pane is a layout slot drawn by its own shader, not a view of a world.
  It goes through the same placement with an image that is already encoded.
- A view that a camera in the world films for a screen applies its own bloom
  and publishes the result directly (`SdfViewSnapshot.BloomStandalone`, the
  `bloom-apply` pass of `SdfWorldPackage`).

An encode added to the `place` pass would miss the single view and the
filmed view. An encode added to the shared placement would encode a pane
twice.

**What we propose.** Shade in linear scene light everywhere, and convert
once, at the point where scene light becomes a working image (option 5b).
The rule is stated at the edges of the render graph, not at one pass.

- **Every image declares what it holds.** There are three kinds, not two.
  *Scene light* is linear luminance in nits with no exposure applied. A
  *window's image* is scene light with a share of its own world's exposure
  already applied. A *working image* is encoded for the display. Each
  consumer declares which it reads, and a graph that joins two kinds
  without the conversion between them does not compose. Part of this
  vocabulary exists. An image source already declares how its values are
  encoded (`ImageColorEncoding`), and one function already turns a source
  into the working image (`ImageSourceConversion.PassOf`). The declaration
  adds what that vocabulary lacks for scene light: its scale in nits, and
  how much exposure has been applied.
- **The conversion is the last stage of the view's own graph**, so no
  consumer can bypass it. Inside it the order is the register's: sharpen,
  then bloom, then the authored tone transform, then the encode. Bloom is
  applied there for every view, which merges today's two bloom paths into
  one. A single view, a split view, a filmed view and a view under a matte
  all publish a working image. The root's `place` pass then only places.
  A pane's contract does not change.
  - *Sharpening moves with it.* The register rules that bloom is added
    "after the direct image is sharpened, so sharpening never acts on
    scattered light". Today `place` sharpens only a view shown at its own
    size, and adds bloom afterwards. Moving bloom into the view and leaving
    the sharpen in `place` would reverse that order. So the sharpen moves
    into the view's conversion too, ahead of bloom, for a view shown at its
    own size. A view that is scaled when placed is reconstructed in `place`
    as today, now from encoded values.
  - *Screen fidelity is an input of the same stage.* An engaged screen
    fades toward its exact pixels by its fidelity. Today `place` blends in
    the untouched source after the sharpen and the tone curve. In the
    view's conversion the same share fades the sharpen, the bloom and the
    tone transform and leaves the encode, so a fully framed screen still
    shows its exact pixels.
  - *A camera read two ways publishes two images.* The register rules that
    a camera used both as scene light and as a direct picture blooms only
    where it is shown directly. So such a view publishes its scene light
    for the first reader and the converted working image for the second.
    The conversion is an added output, not a replacement.
- **A window carries part of its world's exposure, and the viewer applies
  the rest.** The view beyond the glass multiplies its light by its own
  exposure raised to a share of three quarters
  (`SdfPhotometry.WindowAdaptation`), and the view looking through applies
  the remainder of its own. The register adopted that and rejected the
  alternative, "a window published unexposed for the viewer to expose,
  under which a sunlit world seen from a dim room blows to white". An
  earlier draft of this page described the rejected design. The edge is
  scene light on both sides, and the declaration carries the share so that
  the two sides cannot disagree about it. The share is a look, so it moves
  from an engine constant to authored data
  ([authored looks](#looks-are-authored)).
- **A filmed view shown on a screen is a working image.** The screen
  decodes it, like any encoded picture, before multiplying by its
  brightness in nits. The same decode applies where a screen's picture is
  reduced to the glow it casts into its room.
- **Authored colour is decoded by its type, not by where it is packed.**
  There is no single place that packs colour. One grammar for a hex colour
  (`HexColor`) is parsed at 22 places outside the tests. Surface colours
  and the text drawn on screens go through one resolver
  (`WorldBakedColors.Resolve`). Sky, fog and light colours go through
  another (`WorldEnvironmentResolve`). A letterbox colour, a text tint and
  a source's own colour are meant for the display and must stay encoded.
  So the step starts by listing every parse site with what its colour
  means. Then a scene colour and a display colour become two types. Only a
  scene colour can enter shading, and making one decodes it. A site cannot
  then pick the wrong meaning by accident.
- **The encode is the engine's and the curve is the author's.** The encode
  is required and has no parameter. The tone transform before it is authored
  data whose neutral default is no transform at all
  ([authored looks](#looks-are-authored)).
- **The mechanism lands together with the retuned world.** Every place in
  the debug world is tuned against the missing encode. A corrected engine
  with the old values would look wrong everywhere, and AGENTS.md lets a
  look change land only when its captures meet the reference. So the same
  merge carries the mechanism, the standard package's values and the debug
  world's values: the hall's `compensation: 2` goes, and each place's
  exposure and lights are set again against its reference.
- **The lane builds its instruments first.** Two are missing. No grey card
  or luminance patch is authored anywhere. And a capture of a presented
  frame returns 8-bit pixels through the SDR encode, so HDR output has
  never been observed. The lane authors the card and the patches, and adds
  a readback of the presented image in the display's own format.

**Why.** What a pixel value means is a matter of correctness, not of look.
The register already rules that lights, screens and the camera are
photometric: lights in lux, the frame in nits
([register](../decisions/rendering.md#lights-screens-and-the-camera-are-photometric)).
AGENTS.md puts correctness with the engine: "a surface drawn black by a
lighting defect is a defect, not a style". Fixing the conversion repairs the
tone at every brightness level, gives colour one convention, makes the grey
card read correctly and fixes HDR. Every later judgement of exposure,
tonemapping, bloom and materials depends on it. So it lands before the
shading layers migrate to authored programs
([decision 13](#13-shade-through-one-path-and-shrink-the-image-history)).

**What it costs or risks.**

- Every place's look moves at once, which is why the retune is part of the
  same merge and not a later pass.
- A single full-screen view gains a pass it did not have.
- `SdfPhotometry.RangeThreshold` is expressed as a share of display white.
  Unless it is restated, light ranges get longer, and longer ranges mean
  more relight work (decision 7). The step's check reads the lamps' ranges
  before and after.
- The sharpen measures a pixel's headroom against a value of 2, on linear
  exposed light. It keeps reading linear light in its new place, so its
  constants do not change.
- The display encoder has three outputs, not two: SDR in two cases
  (depending on whether the target encodes sRGB when written), scRGB, and
  HDR10, which converts to another set of primaries and applies its own
  curve. A neutral grey passes even if that conversion's matrix is
  transposed, so the check needs coloured patches as well.
- The temporal resolve averages linear samples. It is not changed here, but
  a bright edge in motion is where a conversion change would show first,
  so the check includes one.
- No source we fetched states this requirement in so many words. The case
  rests on the code disagreeing with itself and on the transfer functions
  that `transfer.hlsli` already cites.

**What we still have to find out.** Nothing blocks the fix. Which display
outputs each bar device exposes is read when the instruments exist.

**Options we did not take.**

- *Keep today's values and tune with exposure compensation* (option 5a).
  Tones would be right at one exposure point only. Surface colours taken as
  linear reflect too much light. Screen pictures are multiplied as if they
  were luminance while still encoded. HDR stays wrong.
- *Declare a view's image display-referred by design and fix only the HDR
  branch* (option 5c). This removes the double decode on HDR. It contradicts
  the photometric decision, and a linear scene sample and an encoded pane
  sample would still mean different things inside one image.
- *Leave the sharpen in `place`, after bloom, on encoded values.* This is
  the simpler build, and sharpening an encoded image is common practice.
  It needs the register's bloom ruling amended, because sharpening would
  then act on scattered light. The sharpen's constants would also need
  restating for encoded values. The debug world authors a sharpness of
  zero, so neither order shows there today. This page keeps the recorded
  order. The alternative is listed among
  [the owner's calls](#the-owners-calls), because it reverses a recorded
  decision about what the engine does.

If an author wants today's appearance, they write it as a tone transform
over the corrected conversion, and it is judged on SDR and HDR. The filmic
curve's constants sit in engine shader code today. Under "Authors rule" that
is a leak, and they move into authored data in the same piece of work.

**Evidence.** Code read for this page: the display encode and its three
outputs, the `place` pass's order (sharpen at the view's own size, then
bloom, then the tone curve, then fidelity), its comment, the hex colour
comment and its 22 parse sites, the debug world's compensation, the
terrain pass's grey card, the three paths a view takes, the window's
exposure share and the register's passage that rejects an unexposed
window. From the attack's readers, not reopened here: the two colour
resolvers and what must stay encoded, that a presented capture is 8-bit,
that the debug world's sharpness is zero, and that the temporal resolve
averages linear samples. Reasoned: the 0.104 target showing as 1% of white
where an encode would give code 0.36. Outside sources: Lottes, that
brightness belongs before the tonemap and not as a gamma applied afterwards
(slide 54), confirmed. Gran Turismo 7 blends in a linear space with 1.0
tied to a reference luminance. That claim is partial, because the source
says it of its tone mapping and not of its whole pipeline. That sharpening
encoded values is common practice is unverified.

**What it rests on.** The photometric decision in the register, its bloom
decision, and correctness being the engine's. This is decided by the
evidence, with no owner call: no preference for the present tones keeps the
present conversion.

### 6. Give every receiver the same shadow answer

**The problem.** The shadow a surface receives depends on which shadow map
happens to answer and on what kind of view is asking. A player sees five
separate defects.

*Softness jumps at the boundary between shadow maps.* The first cascade that
contains a point answers for it, with no blending between cascades
(`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-surface-stage.hlsli:254`):

```hlsl
        if (any(abs(plane) > 0.97) || (depth <= c.near) || (depth >= c.far)) {
            continue;
        }
```

The search for blockers and the blur filter are both sized in texels of
whichever cascade answers: `clamp(penumbra, 1.0, SdfMeshShadowMaxTexels)`,
with a cap of 14 texels. With a 60-degree, 16:9 view, a near-cascade texel
is 1.86 cm and a far-cascade texel is 13.9 cm. A shadow's soft edge is
therefore capped at 26 cm on one side of the boundary and 1.95 m on the
other, a jump of about seven times.

*Soft edges are cut short.* The search offsets lie on the shadow map's
plane, and each sample's depth is compared with the receiver's separately
(`sdfMeshShadowGap`). So the 26 cm is a sideways reach. A wide object
directly above a nearby receiver is found at any height. An object whose
edge lies more than 26 cm to the side, measured on the light's plane, is not
found. That removes the outer part of every soft edge wider than 26 cm.

*Past the shadow maps, everything is sunlit.* A point that no cascade
contains returns 1.0, fully lit, as if it had been measured.

*Glass and nested views get no shadow maps.* Whether cascades are read at
all is one flag per view, and glass sets it to false
(`sdf-light-stage.hlsli:189`, `sdf-glass-stage.hlsli:119`). Views of
sessions and views filmed by cameras draw no cascades.

*The wrong light holds the shadow maps.* Two directional lights can cast
shadows, each in a slot. Every directional light whose shadow mode is
`Always` or `Auto` is a candidate for a slot, ranked at `max(0, lux)`. But
the ranking looks at the mode before the brightness
(`src/Puck.World.Client/WorldShadowAllocator.cs:97`):

```csharp
        if (left.Mode != right.Mode) { return (left.Mode == WorldShadowMode.Always); }
```

The day cycle's moon is authored `illuminance: 0lx, shadow: auto`, and the
debug key light is `shadow: always`. The code that fits cascades to a slot
(`SdfMeshShadowCascades.Fit`) checks only that the slot holds a directional
light. So by day an unlit moon holds the second slot and its 40 m cascade.
At night the unlit key light holds the near and far cascades. The
measurement at noon that would confirm this is still owed.

Refusing dark lights would not be enough, because a slot is not a role. The
allocator keeps a selected light in the slot it already holds
(`WorldShadowAllocator.AssignTargets`). The environment writes that slot
number into the GPU's table. `Fit` returns no cascades at all when slot 0
holds no directional light, before it looks at slot 1. So during a handover
with both lights lit, the moon can keep slot 1 while slot 0 empties, and
every cascade disappears.

Two smaller costs come from the same code. Each cascade's bounding sphere
sits on the view's forward axis. A camera turn therefore moves the far
cascade's centre and forces a redraw that could otherwise have been held.
And a program with glass shadows holds three optical-depth buffers for each
shadowed view. They are sized with the shadow tier
(`SdfWorldPackage.WithShadows`), so at the highest tier they are 144 MB of
the 252 MB such a view holds, and every cascade draw clears a whole buffer.

**What we propose.** The outcome: a light is shadowed by the cascade that
contains the point, and by the lighting probes where no cascade does. That
holds per pixel, in every view and on glass. The width of a soft edge
depends only on the light and on the gap between blocker and receiver.

1. **Give cascades to lights by role, and admit a light by what it
   contributes** (option 6b). A light with zero luminance is admitted to
   nothing. The primary role, which carries the near and far cascades, goes
   to the brightest admitted directional light, whichever slot it holds.

   The allocator already ranks the candidates (`WorldShadowAllocator`), and
   today that rank is only printed. Carrying it to the cascade fit is more
   than one edit, because the slot table holds no role (`SdfShadowSlots`)
   and fades are kept per slot.

   A change of role is a transition of its own. Two lit lights can swap
   which is brighter while each keeps its slot. The allocator then sees no
   change of slot and starts no fade, so the near and far cascades would
   jump from one light to the other in a single frame. A role change
   therefore fades as a slot change does. The outgoing light keeps its last
   cascades, tied to the light that drew them, while the incoming light's
   are drawn, and the two are blended over the authored fade time. Where
   there is no spare storage for both sets, the incoming light takes the
   cascades at once and the outgoing light is answered by the probes for
   the length of the fade.

   The data model comes first. Step 4 replaces three named cascades with
   one indexed set. A resolver written against the three names would be
   written twice, so the set's shape is fixed in step 4's memo before the
   resolver is built: a list of entries, each with a light, a role and a
   radius.
2. **Where no cascade contains a point, use the probes' answer for that
   light** (option 6c), per pixel and per slot. "Asked, and no cascade holds
   it" then takes the probes' record of whether that light reaches. Nested
   views and glass become the same rule with zero cascades. A band blends
   from the last cascade into the probes' answer, so the edge of coverage
   is a gradient and not a line.

   What the fallback promises is limited, and the page says so. Past the
   far cascade only the coarsest probe level answers, and its probes are
   32 m apart. A probe that has not been measured yet answers dark, not
   lit. And a residency has one set of probe windows. They enclose every
   view when the views fit inside a level, and otherwise they follow the
   first view (`SdfMeshIrradiance.WindowLow`). A second view far from the
   first is then answered only by the coarsest level that still covers it.

   Steps 1 and 2 are one piece of work, because the probes' answer must
   follow the same light identity as the role. It is checked through a
   continuous exchange of sun and moon in both directions, not only by
   jumping to either end.
3. **Size the shadow filter in metres** (option 6a). The receiver gets a
   sideways search radius, a blocker gap along the light, and a filter
   radius. Each comes from the light's angular size and an authored maximum.
   A one-texel minimum remains, for antialiasing only. Wide samples read
   from a reduced-resolution pyramid of the same cascade. A band blends
   between two cascades.

   The pyramid is a new one. The engine's existing depth pyramid keeps one
   representative pixel for each block (`sdf-mesh-pyramid.hlsli`), which is
   right for ambient occlusion and can miss a thin blocker. The shadow
   pyramid keeps the least and the greatest depth of each block, with the
   body and the layer each came from.

   This comes in two parts. The prototype is a bounded experiment behind a
   switch that leaves the shipped receiver untouched. It compares the
   metre-sized filter with a brute-force filter in a burst of presented
   frames at one stop on the route, to a stated bound on the difference. It
   uses four blockers: one deep and directly above, one displaced to the
   side, one thin and moving, and one whose soft edge spans two levels of
   the pyramid. The production rewrite is one piece of work with the
   transmission plan's `transmit-plane` step, which rewrites the same
   function. It follows `transmit-identity`, which supplies the table of
   bodies and the depth layers that a wide sample must read consistently
   ([transmission's steps](transmission.md#implementation-steps)).
4. **Centre the cascades on the eye and author them as one set of radii**
   (option 6d), with entries beyond 120 m that are held between refreshes.
   This comes after decision 11's caster culling, behind a design memo. The
   memo states the set's data model, which step 1 builds against, and what
   centring on the eye costs in texels at each field of view.
5. **Replace the exit layer with the transmission plan's per-body depth
   layers** (option 6e), on that plan's schedule. The exit layer is the
   second layer each cascade draws today, holding the faces turned away from
   the light.
6. **Clear glass shadow storage only where glass cast into it** (option
   6f), with decision 11's glass list. The buffers are already sized with
   the shadow tier. What is left is the clear of a whole buffer for every
   cascade draw, 48 MiB at the highest tier. The tiles to clear are the
   ones the previous draw wrote, kept as a list with the buffer, so that a
   caster which has left is still cleared. A buffer's first use clears all
   of it. The sum the buffer stores does not depend on draw order, and that
   is kept.

**Why.**

- *Roles* give a whole cascade back by day and give the moon proper coverage
  at night. A shipped game runs sun and moon as one global shadow system
  (Cyberpunk 2077).
- *The probe fallback* stops surfaces beyond the shadow maps reading as
  sunlit. The register already rules that a view with no cascade takes the
  sun's visibility from the probe volume.
- *The metre-sized filter* removes the seven-fold jump, the moving seam and
  the short sideways search. Microsoft's cascade guidance describes a band
  that tests both cascades and interpolates, though it gives no width.
  NVIDIA's soft-shadow notes name only a minimum clamp on the filter. The
  pyramid's nearest published relative is a hierarchical shadow map that
  keeps the least and greatest depth of each block and classifies a region
  before filtering it (Guennebaud, Barthe and Paulin, 2006). That paper
  reports visible steps in a soft edge where the filter changes level,
  which is why the prototype includes that case.
- *Eye-centred cascades* stop any camera turn from ending the far cascade's
  hold. They also replace three named cascades, which the code refers to in
  132 places, with one indexed set, and the terrain's shadow map becomes one
  more entry in it.

**What it costs or risks.**

- The exchange at the horizon must not flicker between lights. A light that
  keeps its slot through a handover must still take the primary role when
  the other goes dark, and two lights that swap brightness must cross
  without a jump in either direction.
- The probe fallback is only as fine as the probe spacing at the edge of
  coverage. A probe volume that has not filled yet darkens the area until it
  does, and a camera cut lands in exactly that state.
- A sample from the pyramid darkens slightly. Wide, sparse filters lean on
  the views filter and the temporal resolve to hide noise.
- A pyramid that keeps only the nearest depth cannot say which body or
  which depth layer a sample came from. So each reduced texel must carry its
  depth, body and layer together.
- Eye-centred cascades give coarser texels to narrow views. The far entries
  need either a refresh spread over several frames or a stated lag at the
  authored day speed.
- The per-body layers need 144 MiB against today's 96 MiB for three
  cascades. The Steam Deck's budget for that is unmeasured.
- The glass sum has a limit. Each shadow texel stores one total of optical
  depth down to its first opaque surface, with the nearest glass entry
  (`sdfMeshShadowGap`). That total is exact in any draw order. It is right
  for a receiver behind all the glass. A receiver standing between two
  separate panes is charged for both when the texel's opaque surface lies
  beyond them. Whether that shows is checked with a receiver in front of,
  between and behind two panes, and the answer decides whether glass needs
  storage by depth.
- The strongest case against the metre-sized filter is that nothing outside
  Puck shows one over a depth pyramid in a moving game scene. That is why
  the prototype comes first.

**What we still have to find out.** The noon measurement of which light
holds which slot. Whether the metre-sized filter matches a brute-force
reference. The far entries' refresh design. The Steam Deck's memory budget.
Whether a receiver between two panes shows the glass sum's limit.

**Options we did not take.**

- *Cached or sparse virtual shadow maps, refitting the projection every
  frame, moment shadow maps, and ray-traced sun shadows* (option 6g). Each
  fails on the moving sun, on the wind, or on the baseline hardware
  ([outside techniques not taken](#outside-techniques-not-taken)).

**Evidence.** Code read for this page: the cascade test, the texel clamp and
the fully-lit return, the search on the light's plane, the per-view cascade
flag, the allocator's ordering and its retention of slots, the moon's and
key light's authoring, `Fit`, the sizing of glass shadow storage by tier,
and the one optical-depth total a receiver reads. From the shadows area's
map: the cascade spheres on the view axis, the glass buffers' 144 of
252 MB, and the 132 references. From the attack's readers, not reopened
here: the probe windows' rule, that an unmeasured probe answers dark, and
the existing pyramid's one pixel per block. Reasoned: the texel sizes and
the 26 cm and 1.95 m caps, the sideways-reach consequence, and which light
holds which cascade by day and by night. Outside sources: NVIDIA's
soft-shadow deck, Microsoft's cascade article and Dybisz on Cyberpunk 2077
(one shadow system for sun and moon, and distant shadows refreshed over
frames), all confirmed. Guennebaud's hierarchical shadow map was opened by
an attack reader and not by this page's verification pass. Unreal's clipmap
levels each covering twice the radius of the last is unverified. No outside
source answers the question behind option 6e.

**What it rests on.** The illumination plan's second decision, and the
register's ruling that a light is shadowed wherever it lights
([register](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)).
A light's softness is its authored `angularRadius`. The widest soft edge,
the blend bands and a far entry's refresh are authored values with neutral
defaults ([authored looks](#looks-are-authored)). Point and spot lights keep
the probes' answer until the illumination plan builds shadow maps for local
lights. This decision does not move that.

### 7. Make a lighting change cost what changed

**The problem.** Any change to the scene relights the whole probe volume
from scratch, however small the change. That is why the lighting lags, and
why a night scene is metered as day after a switch.

A probe stores two things. Its *hits* are the surfaces its rays reach, which
depend only on geometry and are cached. Its *cubes* are small cube maps of
the light arriving from those hits. The relight computes the cubes from the
hits and the current lights.

The part of a probe that could be reused is reused only when two global
version numbers match (`src/Puck.SdfVm/SdfMeshIrradiance.Gpu.cs:307-308`):

```csharp
            if (!candidate.Trace && m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var previous) && previous.Shading is not null &&
                previous.Geometry == snapshot.Geometry && previous.LocalVersion == lighting.LocalVersion) {
```

So any scene change re-traces every probe's shadow rays to every point light
in range. That includes a mesh swapping to a different level of detail
because the camera moved. Six more facts make the cost large:

- **The cost figure is a prediction, not a count.** The scheduler charges
  about 13,800 rays for a fresh probe in the debug world. `RelightRays`
  arrives at that by counting every light whose range reaches the probe's
  origin and multiplying by the number of hits. The kernel is more
  selective: it tests each hit's material, distance, attenuation and facing
  before it asks for a ray. Rays actually issued are not counted anywhere.
- **Light ranges are long.** A light's range is the square root of its
  weight times the brightest exposure any authored key reaches, divided by
  `SdfPhotometry.RangeThreshold` (`SdfPhotometry.RangeOf`). The kernel
  tapers the light to zero at that range. A local light with zero weight is
  left out altogether. The exposure term is the likely reason most of the
  world's point lights reach every probe.
- **Each residency owns a whole volume and its own budget.** At the high
  quality tier that is a table of about 72 MB, in up to four copies, and a
  hit cache of about 90 MB. The table of probe levels is fixed in engine
  code (`SdfMeshIrradiance.LevelsOf`).
- **Every relight shades every hit of every probe**, although neighbouring
  probes hit the same walls.
- **The cubes store absolute light.** A change of colour or brightness is
  therefore a full relight and a fresh convergence.
- **The sky map is rendered three ways.** The GPU kernel renders it. A CPU
  render decides whether a refresh is due. It runs when a procedural sky's
  packed inputs differ from the last candidate's
  (`SdfSkyEnvironmentRefresh.Owes`). A second CPU render runs on the frame
  thread for each lighting capture, when the sky is a source of indirect
  light. A sky backed by an image is refreshed when its acquired picture is
  republished or remapped, which the same decision watches separately from
  the packed inputs.

**What we propose.** The outcome: a geometry change costs a trace. A change
of a light's direction costs a relight. A change of brightness or colour
costs a multiplication, where the light still reaches the same places. A
level-of-detail swap costs only the ray segments it crosses.

The steps, in order:

1. **Fix the round trips first** (decision 4, option 4b), so that GPU time
   is what a measurement actually reads.
2. **Render the sky once, on the GPU** (option 7f). Its identity is its
   packed procedural inputs together with each acquired picture's identity,
   sampling transform and revision.
3. **Store light per source, and combine at publication** (option 7a). The
   relight stores, for each group of sources, the light per unit of that
   group. Publishing a probe combines the groups using the frame's current
   source values. This is built for a stated class of edits:
   - A *source group* is a set of sources that always scale together: each
     shadow-casting directional light, the sky under one gain, each local
     light on its own unless an author binds several to one value, every
     emitting material together, and each screen or opening that lights a
     room. The last kind does not exist in the relight yet. The
     illumination plan makes screens, portals and windows emitters in the
     relight, and that step is designed inside this store, not beside it.
   - *The bounce is stored per group too.* The relight adds a material's
     own emission to a hit, and then reflects the whole previous volume off
     it. Running that once for each group would count emission once per
     group and mix every group's bounce into every other. So each group's
     next bounce reads that group's own previous volume, and emission is a
     group of its own. That history is part of the memory price.
   - A *multiply-only edit* keeps the group's reach, attenuation, direction
     and materials. A directional light's brightness and colour and a local
     light's colour qualify today.
   - *The sky's gain* qualifies once one thing moves. An author can set a
     grid that rounds the lighting's inputs, so that tiny changes do not
     force a relight (`SdfIndirectLightingGrid`). The sky's gain is applied
     first and each sample is rounded afterwards, with a step that depends
     on the largest value. Scaling a stored result is then not the same as
     rounding the scaled input, unless the gain is a power of two. With
     per-source storage a gain change no longer forces a relight, so for
     these groups the rounding moves from the inputs to the combined
     result. The reference drain changes with it, in the same change.
   - A *local light's brightness* qualifies only after a change to a
     recorded decision. Today its range and taper follow its brightness, so
     dimming it changes where it reaches. The register rules that a light
     "ends at a range derived from its intensity". Sizing the range from
     the brightest value the light's authored brightness can reach would
     make a dimmer or a switch a pure scale. It also changes the light's
     falloff at every brightness below that, for direct light as well as
     bounced light. So it is adopted, if at all, for both at once, with the
     register amended in the same change, and after the cost in rays and in
     probes reached has been measured.
   - *Everything else invalidates.* A moved light, a changed cone or range,
     a sky whose shape changed, and a change of material or geometry each
     invalidate what they reach, and those entries relight.
   - *Memory is priced first:* groups times probes times one cube and its
     bounce history, at the debug world's count of lights and screens and at
     a zone's. A world can bind up to 32 screens
     (`SdfProgramBuilder.MaxScreenSurfaces`).
4. **Rescale the probes a relight has not reached yet.** A moving sun is
   not a multiply-only edit, so step 3 sends it to a full relight. While
   that relight works through the grid, a probe it has not reached is
   scaled by the change in the sun's and the sky's mean brightness. The
   illumination plan already holds this as an unbuilt step, and a shipped
   game does the same. It is an approximation that bridges a transition,
   and a relight replaces it.
5. **Light each surface once and share it between probes** (option 7b).
   Each surface entry is lit once, keyed from the exact cached hits. This
   comes after step 3, so that entries are built once per source group. The
   entry's key is defined against the three-word hit the transmission plan
   introduces, and it includes which instance the surface belongs to,
   because that plan's shadow rule needs the receiver's identity.
6. **Store which lights reach each surface as geometry** (option 7c): one
   bit per light per entry, made stale only by the ray segments that a
   changed bounding box crosses. How far a light reaches is sized from the
   brightest exposure any authored key reaches, which is a fixed value and
   not the exposure of the moment. The store is sized by how many distinct
   pairs of surface and light are alive at once, under motion and eviction.
   Query traffic does not size it: one pair asked a thousand times needs
   one entry.
7. **Trace against a mesh that the camera never changes** (option 7d). The
   lighting gets its own class of demand. Its tier is chosen by the probe
   spacing of the finest probe level that reaches the group. That tier is
   the group's floor wherever the floor passes a lighting check. This goes
   with the streaming plan's step 9.

   The lighting check is defined here. It runs at bake time. It compares
   the floor's recorded deviation from the authored field (decision 8) with
   the spacing of the finest probe level that reaches the group. A floor
   passes when its deviation is small against that spacing and it has lost
   no part, closed no hole and joined no two surfaces. A group that fails
   keeps a finer mesh for the lighting. The bake reports how many groups
   passed, failed and were not judged.
8. **Converge coarse to fine** (option 7e): the far level, then the
   storey-sized level, then the finest. A count from a test harness decides
   whether this is adopted. The count that matters is the time until the
   light near the player is right, not only the number of passes.
9. **One lighting budget and one pool of probes across residencies**
   (option 7g): minimums first, the rest divided by pixels presented.
   Probes in open air update at a coarse level's rate. The probe grid covers
   a region of space around the camera. When that region moves, it is
   published slot by slot. The level table becomes authored data in the
   standard package. This goes with the job system's `Lighting` class and
   decision 4's single price.
10. **Light from emitting surfaces as areas, not points** (option 7h). The
    hall's ceiling strips light its floor in blotches. The illumination
    plan already makes screens, portal openings and windows area emitters
    in the relight, citing the standard technique for polygon lights as
    read. This step extends that to static emitting faces. It shares the
    store with step 3's screen groups and with decision 15's screens as
    selected lights, so the three are designed together.

**Why.**

- *Per-source storage.* At fixed geometry, materials and reach, the light a
  surface receives is linear in a source's radiance. So a change of
  brightness or colour is a multiplication, and the stale night metering
  goes away. The nearest shipped relative is cruder and solves a different
  half of the problem: Assassin's Creed Shadows rescales every probe it did
  not update in a frame when the sun or sky changes, from one stored value
  per probe, and keeps the light of local sources out of that rescale. That
  is step 4. Storing light per source, so that a change is exact and not an
  estimate, is the part with no precedent we found.
- *Shared surface lighting* cuts shadow rays and lookups per sweep by the
  number of hits that share an entry. The illumination plan already holds
  this step, with its bar for adoption.
- *Visibility as geometry* stops a level-of-detail swap from relighting the
  whole probe grid, and stops a recoloured lamp from tracing anything.
- *A stable trace mesh* ends the waves of relighting that follow the camera.
  The owner ruled that the trace reads a mesh "which never changes with the
  camera". Either tier meets that.
- *One sky* removes two CPU renderers, one of them on the frame thread. It
  is a duplicate found by reading the code and needs no outside evidence.
- *One pool* ends the cost of a full volume and budget for every world
  shown, and the dark period while the whole probe grid of a newly visited
  area is measured.
  It also moves a look, the level table, out of engine data.
- *Area emitters* fix a defect a player sees in the debug world's largest
  interior, with a technique the illumination plan has already read.

**What it costs or risks.**

- Per-source storage has limits. Two lamps in one group cannot be scaled
  apart. A change in the sky's shape is not a scale. A source at zero must
  still be relit per unit, or switching it on has nothing to multiply. A
  cube and a bounce history per group per probe cost memory that grows with
  the number of groups.
- The strongest case against it is that nothing we fetched stores light per
  source in a dynamic probe volume. Its check is therefore Puck's own.
  After an edit, the cubes must equal a from-scratch drain within
  `ConvergedShare`, in seven cases: a colour-only change, one of two lamps
  changed, a light taken from zero to on, a receiver sitting at the old
  range boundary of a lamp that was brightened, a sky gain that is not a
  power of two with a rounding grid set, a live screen changing its
  picture, and two coloured lamps with an emitting object and a wall lit
  only by bounce, where one lamp is switched off after several bounces.
- The rescale of step 4 has a known error. The illumination plan measured
  one kind of estimate from a probe's mean sky light at about half the true
  light for a window low in a wall. The rescale is the same kind of
  estimate, which is why a relight always replaces it.
- The sharing ratio of step 5 is unmeasured. Its error is bounded by
  measurement, not by proof. A surface entry is valid only where the pool
  already holds one, so a cold arrival has a miss path, and the time until
  a newly visited place is lit correctly is part of the step's check.
- Storing visibility per probe texel would cost about 179 MB, so step 6
  needs step 5's shared entries.
- The hit cache grows. The transmission plan adds a third word to each
  cached hit (`SdfMeshProbeTrace.HitWords` is two today), which takes the
  cache from about 90 MB to about 135 MB. Stores sized before that step
  are sized at three words.
- A floor can cover a wall and still erase a recess, or join two surfaces
  on either side of a probe. Passing the coverage check therefore does not
  qualify a floor for lighting. A group near a fine probe level may need a
  third mesh. A check that only asks whether the light stays the same as
  the eye moves cannot catch a mesh that leaks the same amount everywhere,
  so the trace mesh is compared with the light traced against full detail.
- The probe level table stays engine data until step 9, because fixing its
  authored format before the pool is designed would fix it twice. Until
  then every store and tier that depends on it reads it through one
  function (`SdfMeshIrradiance.LevelsOf`), and each is also sized against a
  table that differs from the default, so that none is quietly tuned to
  today's 2.4 m storey.
- Indoors, a coarse-first start may look poor.
- For the sky, the refresh decision lags a readback. Identical packed
  inputs can be bound to new pixels, so a key without the picture is wrong.
  A picture that is cold or absent must stay an explicit state.
- For the pool, a small share starves without a minimum. A probe that is
  put to sleep lights a passing body with older light, and needs something
  to wake it when the scene inside its region changes. A pooled volume may
  serve only viewers who are permitted the same picture
  ([decision 14](#14-treat-a-nested-view-as-the-same-view-at-another-size)).
- An area emitter's light can be blocked, and the standard technique for
  polygon lights does not include that. How an emitting face is shadowed
  is an open design question inside step 10.

**What we still have to find out.** The rays actually issued. The sharing
ratio. The memory cost of per-source cubes with their bounce history.
Whether floors pass the lighting check. Whether moving the rounding grid to
the combined result changes anything an author relied on.

**The option we did not take yet.** The probes' cached hits live in memory only.
Keeping them in the store, identified by their inputs like a baked mesh,
would save a visited place its trace. It is priced after step 7, because
until the trace reads a mesh the camera never changes, a hit depends on
which mesh happened to be loaded.

**Evidence.** Code read for this page: the reuse condition, how
`RelightRays` counts against what the kernel tests, the range formula and
the taper, the sky's three renderers, the order in which the kernel adds
emission and the previous volume's bounce, the sky gain applied before the
rounding grid, and the register's passage on a local light's range.
Measured: the 13,800-ray charge. Reasoned: the 72 MB table and 90 MB hit
cache, the linearity of transport, the 179 MB for per-texel visibility, and
135 MB for a three-word hit. From the attack's readers, who downloaded the
deck: that Assassin's Creed Shadows rescales unselected probes on a sun or
sky change, keeps local lights out of that rescale, and halves its probe
budget at 60 frames a second. From the illumination plan: its unbuilt
rescale step, its area emitters, and its measured 0.49 for a window low in
a wall. Outside sources, confirmed: Apers, that one title on Frostbite's
system freezes its lighting to hold 60 Hz (the attack adds that this
title's lighting is fixed, which was not reopened), Brixelizer's screen-fed
cache taking effect "only when they are in view", GI-1.0's leak flag in its
key, and the Spatial Hash Radiance Cache's 160 MiB at 2^22 elements.
Partial: Majercik's hysteresis rule. Unverified: Lumen's "we cache geometry
here instead of lighting", Apers's light-space sample at probe hits (slide
27), Majercik's 30 to 50% saved by sleeping probes, and Brixelizer's nested
volumes sharing one atlas.

**What it rests on.** It extends the illumination plan's first decision by
one level and keeps a drain a function of its inputs.

### 8. Choose detail by one error budget, along one chain of meshes

**The problem.** Two separate rules decide how detailed a mesh is drawn,
they disagree, and nothing cheap stands in for an object that is far away.

A group has two kinds of coarser mesh. A *tier* is a fresh mesh built from
the field on a coarser grid. A *level* is a simplified copy of one tier's
mesh. Three constants across two rules choose between them:

- A group's tier is the coarsest grid whose cell covers at most half a
  display pixel (`SdfMeshTier.PixelShare`, 0.5) at the nearest point that
  asks for it.
- A level is drawn where its error covers at most one pixel on screen
  (`SdfMeshDetail.CoarserPixels`, 1). The levels are built to error budgets
  of a quarter of a cell and two cells (`SdfMeshCollapse.CellShares`).

The two rules also measure "size on screen" differently. The tier rule uses
straight-line distance over the focal length (`SdfMeshDemand.PixelAt`). The
level rule uses depth along the view direction, stretched for the corners of
the view (`SdfMeshDetail.Of`).

Four consequences follow.

- **The finest mesh is almost never drawn.** At the tier it asked for, a
  group's first coarser level always qualifies and its second qualifies
  about half the time. The full mesh, level 0, is drawn only for boxes that
  touch the eye plane, for skinned and glass draws, and for groups already
  at their finest tier.
- **It still occupies memory.** The full mesh's index and material data
  stay in GPU memory: about 1.1 GB of a 3 GB mesh region at 71 million
  triangles. Building the other levels takes 28.7% of the bake's CPU time.
- **One demanding copy sets the detail of every copy.** A shared prototype
  is baked once, at its largest placement scale (`WorldRigidAssets`), and at
  the finest tier that any copy asks for from any view that asks for detail
  (`CopiesPixel` in `SdfMeshPathBake`). That is usually the nearest copy,
  though a large copy far away can ask for more than a small one nearby.
  One fern beside the player sets the detail of every fern in the world.
- **Nothing stands in far away.** The levels stop at a two-cell error, and
  no coarser mesh exists for a distant draw.

**What we propose.** One error budget, measured in display pixels for a
view or in texels for a shadow map. One function that projects an error to
the screen. One chain of meshes per shape, in which every step, or *rung*,
has been measured against the authored field.

1. **Count what is drawn** (the first half of option 8b), in stage 0.
2. **Give every use of "size on screen" one function** (the rest of option
   8b), in stage 3 with the chain. The code computes size on screen in four
   places today: for a group's tier (`SdfMeshDemand.PixelAt`), for a
   mesh's level (`SdfMeshDetail.Of`), for how often a body updates
   (`WorldCreationInstances`), and for a viewport's pixels per metre
   (`WorldSeatViewports.PixelsPerMetre`). The register already puts levels
   and update rates under one measurement. The one function has one home,
   and all four call it. It measures by straight-line distance, so that
   turning the camera in place never changes a mesh's detail. The shader
   does not get a hand-written copy: the host computes the scale and passes
   it as data, as the level rule already does. After this, either the two
   rules are re-pitched against each other, or the full mesh's own data is
   stored as a separate run that can leave memory.
3. **Audit each tier before building anything**
   (`puck mesh audit --cpu --tier`).
4. **Build the chain** (option 8c). For each shape it holds the levels of
   the tier that was asked for, then the floor, then the tiers other copies
   ask for. Each copy and each cascade reads the coarsest rung that
   qualifies for its own measure. The streaming plan's floor is the first
   rung added.
   - *What a rung records.* A rung enters a chain only with a recorded
     deviation from the authored field. The field is the one reference
     every tier shares. The measure exists: `SdfMeshFieldLevels.Measure`
     bounds the distance between a level and the field's surface in both
     directions, and covers lost parts and lost regions of material or
     normal. The record is stored with the rung's mesh, under the same
     identity.
   - *Where it looks.* The measure runs over the authored group's own
     bounds, not over the bounds of the mesh being judged. A check that
     starts from the output mesh cannot see what the mesh lost: if one of
     two separate parts vanishes, the mesh's box shrinks to the part that
     is left and the check never samples where the other part was. Today's
     audit builds its sampling grid from the mesh's box
     (`SdfMeshAudit`), so it stays a diagnostic and does not qualify a
     rung.
   - *What a rung cannot prove.* The measure samples. A feature thinner
     than its sampling, or two surface crossings between two samples of the
     same sign, can be missed. A rung whose group has such features is
     recorded as not qualified, and its consumers keep the finer rung.
   - *Stepping down.* A consumer steps down to a coarser rung only when
     that rung's recorded deviation fits its budget in pixels or texels.
     Otherwise it keeps the finer rung. A switch between two rungs is
     budgeted on the sum of their deviations, because one rung can err
     inside the true surface and the other outside it.
   - *A condition inherited from the virtual geometry plan.* That plan
     asks for appearance to be filtered at coarse levels (lost normals
     folded into roughness, lost materials into a blend) before any chain
     deeper than today's is built. A rung coarser than the floor waits for
     the same thing.
5. **Thin ground cover with distance** (option 8g): each placed plant has a
   stable rank, and an authored curve says which ranks remain at each
   distance. The rank comes from the occurrence's identity
   ([decision 12](#12-keep-motion-and-placement-as-authored-programs-at-zone-scale)).
   The curve is an authored curve row of the kind gait drivers already read
   (`WorldGaitDrivers.CurveHeight`). The ground's own shading program
   carries the coverage that is lost. This comes after decision 11's
   instance table.

The floor then does three jobs. It is what shows when a mesh has not
arrived, what a far cascade draws, and where a distant copy ends up. The
lighting reads it only where decision 7's lighting check passes.

**Why.** Three constants become one budget and how it is divided. Far
copies and far shadow casters stop drawing the nearest copy's tier. Nanite
keeps its coarsest page in memory at all times for the same reason. The
coarsest tier of the debug world has about a twentieth of the triangles of
its finest, so the saving for distant draws is large. Puck has an advantage
a mesh-only engine lacks: every rung can be checked against the authored
field, so no separately authored stand-in mesh is needed.

**What it costs or risks.**

- Keeping several tiers in memory costs bytes. They come out of the mesh
  library's one budget.
- Auditing every rung costs bake time. The audit has a limit stated before
  it is built: if recording the deviations adds more to the cold bake than
  the chain's draws save, the chain stops at the floor.
- A rung that fails its audit keeps its group at a finer tier than the
  chain promised.
- Re-pitching the rules changes the look. The full mesh cannot leave memory
  before the lighting stops tracing it (decision 7, option 7d).
- The errors of two tiers cannot be put in order without measuring both. A
  collapse's error only grows along the simplifications of one contour
  (`SdfMeshCollapse.Levels`), and a level can be missing. A tier that was
  contoured afresh starts from different geometry. The floor's acceptance
  test certifies coverage, not deviation. So a coarse rung can keep a wall
  and still move an edge further than the budget allows. This is why every
  rung is measured against the one reference.
- A floor that passed only its coverage test may be drawn where a mesh is
  missing, as detail that is visibly owed. It is not a rung, and nothing
  steps down to it by choice.
- Thinning must remove whole plants, never fade pixels, and the default
  curve keeps everything. It must not pop in a presented walk, in the
  camera's view or in a shadow.

**What we still have to find out.** How many triangles each level and tier
actually draws. Whether recording a rung's deviation costs too much bake
time. Whether switching tiers per copy shows as popping on thin parts in
motion.

**Options we did not take.**

- *Keep both rules* (option 8a). The overlap described above remains.
- *More collapse levels* (option 8d). Already measured and rejected
  ([item 4 of the disagreements](#where-the-research-disagreed-and-what-we-did-about-it)).
  A collapse never changes a mesh's topology.
- *One mesh baked over many copies* (option 8e, the virtual geometry plan's
  D3 and G7). It lowers draw counts for arrangements that occur once, at the
  price of unique geometry per arrangement. It becomes a measured exception
  for repeated arrangements, compared against the chain and not adopted
  before it.
- *A hierarchy of clusters inside a draw* (option 8f, D4). It helps large
  draws that are partly visible. It is a second mesh builder, and no bar
  device has been shown to be limited by geometry. The virtual geometry plan
  keeps watching it.

**Evidence.** Code read for this page: the three constants, the two
projection formulas, the shared prototype's bake at its largest scale, the
rule that takes the finest demand over a prototype's copies, the existing
measure of a level against the field, and the audit's sampling grid built
from the mesh's own box. From the attack's readers, not reopened here: the
other two places that compute size on screen. Measured: the collapse
at 28.7% of bake CPU, and the coarsest tier at about a twentieth of the
finest's triangles. Reasoned: which draws take the full mesh, and the 1.1 GB
of 3 GB. Outside sources: Karis on Nanite, that error is imperceptible only
"so long as the error is subpixel" under temporal antialiasing, and that its
root page is always resident, both confirmed. That Nanite picks a cut per
cluster by projected error is partial: the cut is a test between parent and
child, and the source does not say each instance selects its own. Evans on
Dreams, that thinning per pixel breaks temporal history (slide 118), is
unverified.

**What it rests on.** The owner's one-pixel and one-texel rulings
([register](../decisions/rendering.md#a-raster-pass-draws-only-what-it-can-show)),
a derived product being a cache identified by its inputs, and
"representations are chosen by measured cost". The budget values are quality
settings. The rulings' values stay the engine's defaults, and an authored
quality table may choose others ([authored looks](#looks-are-authored)).
This is the one look value on this page whose engine default is a recorded
ruling and not a neutral value.

### 9. Give memory back, and never show a hole

**The problem.** GPU memory for meshes only grows, and a mesh that is not
in memory leaves a hole in the picture. According to the streaming area's
map of the code:

- No group ever leaves the mesh region.
- A mesh that was refused, or has not arrived yet, is a hole.
- Relieving memory pressure means loading a coarser tier beside the fine
  one, which costs more memory first.
- The region grows by copying, up to about 4 GiB.
- The main camera asks for detail by distance alone. Detail that is hidden
  holds memory, while detail that is visible is what gets reduced.

Nothing here needs a new design. The streaming plan's decisions 4, 5, 7, 8
and 9 stand. This round decides what sizes them and adds three things.

**What we propose.** Follow the streaming plan in its own order (option
9a): the floor, then eviction, then keeping what was observed in use, on a
memory backing that can return memory to the system. The round adds:

1. Decision 8's counters come first, because they decide how large a floor
   and a detail tier are.
2. The estimate of an upload's GPU time becomes a rate measured per device
   (decision 4). The bake's GPU time is already charged that way, though its
   jobs are not yet cut to it.
3. Requests for detail run ahead along the path the camera program can
   take, and cancel what leaves reach. The streaming plan already places
   this in its step 10.
4. What shows before a floor exists is stated. A floor that is kept in
   memory covers a group that was evicted. It cannot cover a place the
   player reaches by a cut nobody predicted, with an empty store, because
   that floor has not been baked yet. Until the new place's floors and
   lighting stand, the frame keeps the last complete scene or shows the
   window's authored fallback image. It does not publish a scene with
   holes. Three cases are counted apart: a floor that is not ready yet, a
   floor whose bake failed, and a floor that was evicted.
5. One alternative is weighed in the stage's own measurement. Under memory
   pressure the bake coarsens every group's demand by one shared amount
   (`SdfMeshTier.Biased`). That is fair to every group by design. The
   alternative coarsens first where a byte removes the least visible error.

Eviction is judged by physical bytes. Deleting a group's rows can free
nothing while a reader still in flight, or a live neighbour, pins its block
of memory. Only the contiguous fallback backing is enabled in the current
tree. So a count of evictions proves no relief. The stage that lands
eviction carries the streaming plan's own acceptance test
([its qualification table](streaming-and-memory.md#sparse-backing-design-for-review)).
The test takes a real reduction of the budget and measures the peak and
settled native bytes held, the reservations outstanding, and the retirement
of the last reader. It runs with a shared block and with the fallback
forced. It has two halves, and both must be seen. In the first, at least
one block is known to be releasable once its last reader retires, and the
test requires an observed release and a matching fall in the bytes held. In
the second, something is deliberately in the way, and the report accounts
for the bytes that stay pinned or stranded. Without the first half, an
implementation that never frees anything could pass by always naming an
obstruction.

**Why.** Nanite's authors give the reason for a floor: "Always having the
root page means we always have something to render". Requests carry a
priority, and low-priority pages are evicted. The owner has ruled the same
outcome for Puck: a group that has not landed or has been evicted "shows its
coarsest level ... never a hole"
([residency](../decisions/residency.md#the-owners-rulings-on-look-and-policy)).

**What it costs or risks.** A floor can contour a thin wall away to
nothing. Feedback about what was used arrives a few frames late.

**What we still have to find out.** Decision 8's counts. Whether the code
still fetches too much, or refines too slowly, once observed use decides
what is kept.

**Options we did not take, or hold.**

- *Let the GPU's culling pass alone request arrivals* (option 9b), as Nanite
  does. A group that is not in memory cannot report that it is needed, and a
  camera turn has no history to draw on.
- *Pages or slabs below the mesh that shaders read through* (option 9c). It
  puts an extra read in every vertex fetch. The slab prototype's read cost
  about a fifth more on the surface pass.
- *Hints from geometry already in memory* (option 9d). A drawn floor or
  parent would report that a finer tier would have been used, through the
  observation channel the plan already builds, beside the CPU's prediction.
  The streaming plan keeps prediction for arrivals and reads back what was
  used
  ([streaming decision 7](streaming-and-memory.md#decision-7-prediction-decides-arrival-and-observation-decides-retention)).
  A hint arrives a few frames late and must never weaken the floor. It is
  held as an experiment. It opens if over-fetching or slow refinement is
  still measured once observation decides what is kept.

**Evidence.** From the streaming area's map: the five facts in the problem.
Measured: the slab prototype's extra fifth. Outside sources: Karis on
Nanite's root page and its page priorities, confirmed.

**What it rests on.** The streaming plan's decisions and the owner's ruling
quoted above.

### 10. Make a bake cost what changed, and close the contour defects

**The problem.** A change to the bake's own code costs a whole bake. The
whole bake is just over its figure. And some shapes bake wrongly.

The owner's ruling on waits sets the order of those three. The wait that
costs most is the one between making a change and seeing it, because it
breaks an author's or a developer's flow. A long start, such as a first
bake, is tolerated if it stays reasonable for what it is. The figure of
about a minute for a cold bake exists to stop that time drifting, since
bake times once grew to nearly an hour. It is not a finish line.

- **What each kind of edit costs today.**
  - An edit to a `.puck` document rebakes only the creations it changed. Its
    wait is elsewhere: compiling the debug world's documents takes about
    15 s, and installing a reload stalls the tick that adopts it
    ([open items](open-items.md#edit-to-running-hot-reload-and-boot)).
  - An edit to a shader that the bake does not run recompiles only the
    kernels that include it, and rebakes nothing.
  - An edit to code the bake does run rebakes every mesh. A stored mesh's
    key is one identity for the whole store. It hashes the C# code the bake
    reaches, the compiled bytecode of the bake's two kernels, any hot
    reload that was applied, and the entry format's version
    (`SdfMeshBakeIdentity`). The two kernels hold the whole field
    interpreter. So one line changed in the collapse, or in one field
    operation, makes every stored mesh a miss, and the developer waits for
    a cold bake.
  - Nobody has measured these three waits end to end under one recipe.
- **The cold bake.** A quiet cold bake takes just over a minute on both
  backends (decision 4). What limits it under the recipe has not been
  measured
  ([item 2 of the disagreements](#where-the-research-disagreed-and-what-we-did-about-it)).
  The Land stage is the CPU step that turns a contoured mesh into its
  stored form. It takes 34% of the bake's processor time, and building the
  coarser levels inside it takes 28.7%.
- **Memory.** The process's private memory passes 10 GB in the first minute.
- **Two stores.** Each graphics backend keeps its own mesh store, because
  the kernels' bytecode differs per backend and is part of the identity.
- **Contour defects.** Six of the fourteen blend exhibits fail the mesh
  audit. The Moth's shin groups carry seven tenths of her wrongly facing
  triangles, where a blade tapers to a rim thinner than the grid cell
  ([bake detail](open-items.md#bake-detail)).
- **Wasted triangles.** A grass blade part is a prism. It is baked at the
  2 mm minimum cell and draws about 474 triangles, where its actual
  polyhedron has about a dozen.

**What we propose.** The outcome: a change costs waiting in proportion to
what it touched. The cold bake stays near its figure, read at the close of
every stage so that it cannot drift. There is one mesher, which is asked
again wherever a cell cannot hold its features. A cheaper way to build
levels is judged by auditing each drawn level against the field, never by
equality with the old output ([rule 4](../../AGENTS.md#core-rules)).

1. **Measure the wait for each kind of edit** (decision 1): a `.puck`
   document, a shader and C#, from saving to seeing it, by phase, with the
   work each did.
2. **Key each stage's product by what that stage read** (the first half of
   option 10f). The bake has three stages: sampling the field on the GPU,
   contouring the samples into a mesh, and the Land stage that simplifies
   and stores it. Today one identity covers all three. Keyed separately, an
   edit to the Land stage starts again from stored contours and does not
   sample the field again, and an edit to the contouring does not sample
   again either. Two things suggest this is within reach. The bake's code
   identity already hashes its parts separately (`SdfMeshBakeClosure`), and
   the store already says which part changed when a mesh misses
   (`SdfMeshStore.ExplainMisses`). Open items already asks for the bake to
   be split into its stages. A narrower key still, by which field
   operations a group's program uses, is a later question. It depends on
   whether the interpreter's source can be attributed to each operation,
   which nobody has looked at.
3. **A contour rule for a cell that cannot resolve its faces** (option 10a).
   Where the gradient at a surface crossing is a blend of two faces, that
   crossing contributes no plane. A row of cells beside a crease takes the
   crease's vertices. A face narrower than the cell either closes to a
   single crease or is sampled more finely.

   The finer sampling already exists. The bake refines a cell that it
   flags (`SdfSampledLattice.Refine`, `SdfDualContouring.ExtractRefined`).
   So the rule is a new trigger and a new vertex rule inside those two
   functions, not a second path.
   Its first task is a count: for each group that fails today, whether the
   failing cell was ever flagged. If it was not, the defect is a trigger
   that never fires, and the fix is smaller than a new rule.
4. **Remove triangles without changing the surface** (option 10b, the
   visibility plan's V3a). Drop runs of collinear outline vertices on
   meshes that touch no other mesh. Convert a part exactly where its program
   proves the surface has a closed form. This cuts a blade's triangles more
   than ten-fold, in the mesh pass and in every cascade.
5. **Then whichever speed fix the critical path ranks first.** There are
   two candidates:
   - the bake's GPU sampling in one round trip per stage (option 10c, which
     is decision 4's option 4d);
   - the Land stage (option 10d). Five changes: reject a simplification step
     by checking the removed vertex first. Share one set of error samples
     between the two routines that measure a step. Limit the second pass
     that closes thin sheets to samples near what the first pass closed.
     Schedule chunks on the one thread pool. Give each worker its own
     storage.

   The run that ranks them is taken at the close of stage 1, because steps
   3 and 4 change what the bake costs. It records worker activity, the wait
   on each GPU request, GPU timestamps and the total time together, on a
   quiet machine with the display composing and an empty store.

   Two limits apply to the Land stage's changes. The order of chunks
   follows demand before size: a group the player can see bakes before any
   unseen one, as the job system plan requires, and largest-first applies
   only among chunks of equal priority. And the cheaper collapse is tried
   before any replacement for it. The visibility plan describes a faster
   simplifier both as taking the collapse's place and as standing beside
   it. This page settles the order: cheapen the one collapse first, and
   measure a different error measure inside it before considering a second
   simplifier.
6. **One store that both backends read** (the second half of option 10f),
   keyed by what a mesh is derived from and not by a backend's bytecode.
   This is judged separately from the cold bake's figure. That figure is
   each backend's own bake from an empty store, timed with the other
   backend's store absent. Reuse across backends is then proven in both
   directions, by geometry, materials, seams and appearance in motion, on
   each bar device. Each entry records which backend derived it. Whether
   the bake kernel's floating-point results reproduce across GPU vendors is
   read in the same check.

Steps 2 and 6 change what the register says a mesh's identity is: "the
methods its call graph reaches and its kernels' bytecode"
([register](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
That passage is amended in the change that adopts either.

Edits to the bake's kernels land in batches, because until step 2 every such
edit forces a whole bake.

A contour fix is judged over the whole world and by more than its count of
wrongly facing triangles. The rule reaches every bake, so fourteen exhibits
are not enough: an earlier attempt that narrowed the lattice along one axis
was worse over the whole world. An empty or missing patch also lowers
the count. So the check is the stored-mesh audit of decision 1 over every
group, with no class of defect worse than before, every audited group
non-empty, the `open` and `uncovered` measures not risen, and the seams
looked at from the sides that fail today. The audit's own blind spot is
stated with it: it samples around the mesh it is given, so a part that
vanished entirely is found only by the measure that starts from the
authored shape (decision 8).

**Why.** A developer who waits a minute after every edit to the collapse
makes fewer edits to the collapse. Keying by stage attacks that wait
directly, and it uses the principle the store already rests on: a derived
product is identified by its inputs. Steps 3 and 4 each stand alone and fix
defects that block authoring: failing exhibits, and places that cannot have
levels built. For the speed fixes, neither has evidence that it is on the
critical path, so measuring first avoids building the wrong one.

**What it costs or risks.**

- Stored contours cost disk and memory, and the bake's private memory is
  already too high. If a stage's product is too large to keep, that stage
  is not split.
- A key that is too narrow is the dangerous direction: an edit that should
  have invalidated a mesh and did not shows as a stale mesh that nothing
  reports. Each stage's key therefore includes everything that stage's code
  reaches, as the whole key does today, and the split changes only which
  stage a part belongs to.
- The cold bake is over its figure by only seconds on the measurements that
  exist, and step 3 spends bake time on finer sampling. Each fix therefore
  lands with the bake's time reported beside it, and the figure is read
  again at the close of stages 1, 2, 3 and 4.
- Removing triangles helps flat and closed-form parts only.
- A shared store has three hazards. The same shader source can compile
  under different options or generated interfaces. Whichever backend writes
  an entry first can hide which backend derived what is drawn. And a warm
  hit from the other backend's store is not a cold bake. The work packet
  itself warns that the backends' forest runs differ in total triangle
  count, so "a shared identity alone does not halve the cold cost"
  ([bake proportional to change](open-items.md#bake-proportional-to-change)).

**What we still have to find out.** How long each kind of edit takes to
show. What limits the cold bake. How large each stage's product is.
Whether a failing cell was ever flagged for refinement. Whether the new
contour rule holds where a surface folds or a feature disappears: no source
covers those, so that evidence is Puck's own to produce.

**The option we did not take.** Contouring on the GPU (option 10e) would be
a second mesher. No source measures it, and nothing shows the CPU is the
limit.

**Evidence.** Code read for this page: what the store's identity hashes,
that the code identity keeps its parts and the store explains a miss by
part, and the register's passage on a mesh's identity. From the edit
procedure and open items: what each kind of edit rebuilds, the 15 s
document compile and the reload's stall. From the bake area's map: the
10 GB of private memory. From the attack's readers, not reopened here: that
the refinement takes the finest split that fits and is not skipped
silently. Measured: Land at 34% and the collapse at 28.7%. Reasoned: 474
triangles against a dozen. Outside sources, confirmed: Ju and others, that
they chose the cut-off for small singular values in their vertex placement
by experiment, and Schaefer and others, that a manifold criterion "may
still contain intersecting polygons". Ju's remark is about that one
threshold. It says nothing for or against a rule that drops a blended
gradient's plane. No source covers two creases in one cell. Unverified:
Kapoulkine's clustering pipeline falling from about 9 minutes to under 3 on
the same class of fixes as option 10d.

**What it rests on.** The owner's ruling on waits, a derived product being
a cache identified by its inputs, and the rule that there is one contouring.

### 11. Keep one instance table, and test it once for every pass

**The problem.** The cost of submitting draws follows how many objects
exist, not how many a pass shows. Different passes decide visibility in
different ways.

- **Every candidate costs a command, drawn or not.** Each phase of each
  pass writes one draw command per candidate, "empty where it draws
  nothing", over every staged row
  (`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-occlusion.comp.hlsl:17-18`).
- **Only the far cascade is culled by who will read it.** It alone gets a
  mask of the receivers that can read it and a two-phase occlusion test
  (`src/Puck.SdfVm/SdfWorldPassRecorder.cs:898`). Its mask marks every
  covered camera pixel, although the surface pass reads the far cascade only
  where the near one does not contain the point.
- **Shadow maps cost more than the picture.** At the lobby the two cascades
  take 3.9 ms against 1.7 ms for the main mesh pass.
- **Skinned draws are never culled or simplified.** A draw whose vertices
  follow several member transforms has no bounding box
  (`SdfMeshBox.TryOf`). It draws its full mesh in every pass.
- **Draws are not indexed**, so the vertex shader runs three times for
  every triangle.
- **Glass visibility is decided twice**, once on the CPU and once in the
  kernel.

**What we propose.** One table of instances, each with a stable identity.
One kernel that classifies the table for every pass. A command list equal
to what survives.

1. **Keep the visibility plan's order, and give each step a stage.** That
   plan orders four steps, and this page had placed only some of them.
   Stage 2 takes the first three:
   - Far placements update less often (V2a).
   - Only instances that changed are uploaded, and a rigid draw reads its
     transform slot directly (V2b, which is the virtual geometry plan's
     G4). This comes after the 2,600 records re-uploaded at rest have been
     explained. It is frame-thread work: it makes record staging follow the
     bodies that moved.
   - The command list is compacted, in order, to the draws that survive
     (V1a).

   Stage 3 takes the fourth: grouping by geometry and level (V1b).
2. **Cull shadow casters by which receivers will read them, in every
   cascade** (option 11b). The first step applies a receiver mask to the
   near and second cascades under today's rule. Under that rule an opaque
   surface reads the first cascade that contains it, and glass reads none.

   The mask is then made a conservative union by construction, in two
   parts.
   - *What it covers.* It marks every cascade texel a receiver could read:
     each slot, each cascade in the blend band, and the widest footprint
     the search and the filter are allowed. The marking cannot simply call
     the receiver's own function. The receiver sizes its filter from the
     blockers it finds in the shadow map, and the marking runs before that
     map is drawn. So the two share the geometry of the bound and not the
     data: the marking uses the authored maximum radius, and the receiver
     stays inside it. Where no maximum is authored, the bound comes from
     the light's angular size and the cascade's depth range. Where no
     finite bound can be shown, that cascade culls nothing. Glass
     receivers mark from the list of glass draws.
   - *How long it holds.* A far cascade is drawn once and shown for
     several frames. A mask built from one frame's camera is then stale
     for the rest: if the camera moves sideways, or a foreground object is
     removed, a surface comes into view whose only caster was culled when
     the map was drawn. So a mask belongs to one layer and one projection,
     and records the region it is valid for. A receiver outside that
     region takes the probes' answer (decision 6) until the layer is drawn
     again, and a count of such receivers asks for the redraw. A cascade
     with no mark yet keeps every caster, which covers the first frame
     after a camera cut and a view that has not rendered.

   Decision 6's filter and probe fallback each land together with the
   mask's extension for the reads they add.
3. **Bounding boxes for skinned draws** (option 11d), in the virtual
   geometry plan's two steps. A draw whose vertices each follow one member
   gets its box from each member's box under its live transform (G3a), and
   that runs beside step 2. A draw with blended vertices stays unboxed, and
   so never culled, until the moving-blends work publishes how far a blend
   can displace a vertex (G3b).
4. **Compact the commands in order** (the first half of option 11a, V1a),
   in stage 2. Ordered compaction needs a parallel prefix scan and an
   ordered scatter. Puck has two compactions already, and neither fits:
   the probe client's runs in series, and the shading bins' scatter keeps
   no order. The scan and the scatter are written once, as a shared kernel
   header that the other two can adopt.
5. **Group the commands by geometry and level on a stable identity** (the
   rest of option 11a, V1b), in stage 3. The grouping works on today's
   draws, which fetch their own vertices. It does not depend on indexed
   draws. The draw record is split in the same piece of work (option 11e),
   so the record's format changes once. Where the frame's origin sits is
   decided in that design too: the composition passes a render origin of
   zero today (`SdfCompositionFrameSource`), and the valley plan subtracts
   an exact origin for the same consumers. The cascade and history designs
   read the same origin.
6. **Indexed draws, as an experiment** (option 11c). It is a switchable
   variant over the same commands, measured pass by pass. It needs three
   things stated first. A triangle's identity leaves the vertex stage as a
   per-vertex value today, and shared vertices cannot carry it, so the
   fragment stage must supply the primitive's id. The Vulkan backend
   enables no feature for that today (`VulkanShaderCapabilities`,
   `VulkanLogicalDeviceFactory`). And a pass that reads no triangle id,
   such as an opaque cascade, can be indexed without it. Indexed draws
   become the baseline's format only if every bar device supports them and
   none is slower. If they pay on some devices only, they are an
   enhancement there under the owner's rule for stronger hardware.
7. **Read a residency's instance rows once per frame and share them
   between its views** (option 11f), after the grouping. Each view and each
   cascade keeps its own lists and history. The glass list comes from the
   kernel.
8. **Smaller per-pass costs, each on its own measurement** (options 11g and
   11h): an early-depth variant, a packed render target, a depth pyramid
   built in one pass, and culling of clusters inside a draw. Clusters are
   admitted only by a measurement on a bar device, taken on a cascade
   pass, where the published gains are largest.

**Why.**

- *Compaction.* Empty indirect draws "are actually not free". One
  measurement went from 2.97 ms to 0.9 ms over 125,000 culled objects on a
  GTX 1070. The step removes the walk over every command in every phase of
  every view, and stops the command count growing with the population.
- *Receiver culling.* At one stop, the far cascade's full refresh fell from
  23,125 draws to 4,138, and from 8.5 ms to 2.9 ms. Today the near and
  second cascades are culled by bounding box only.
- *Indexing* could remove vertex work repeated per corner in every raster
  pass. No source we read measures indexed draws of this kind, and one
  published test found them slower on one GPU and no different on an RTX
  2060. That is why it is an experiment.
- *Skinned boxes* stop characters, and the skinned draws at the debug
  world's falls, drawing at full detail everywhere.
- *Record split.* About 2,000 records are re-uploaded each frame at one
  stop. The split removes re-uploads for each body that moved.
- *Shared rows* remove dispatches, barriers and two CPU copies of the test.

**What it costs or risks.**

- **The receiver mask must cover every reader this page adds, for as long
  as the map is shown.** Today's mask starts from the opaque camera mesh
  (`sdfReceiverMark`). It would miss both cascades inside decision 6's
  blend band, the footprint of the blocker search and of the filter, both
  shadow slots, and glass, which has no opaque depth where a pane stands
  against the sky. An off-camera caster that shadows only that pane would
  be culled. That is why the union is built by construction. The exit
  layer's share of the cost and the counting fragment stage's share are
  measured before either changes.
- A mask step can look successful and save nothing. The occlusion kernel
  writes a command for every candidate, empty or not, so hiding every
  caster leaves the GPU walking the whole list. The mask is therefore
  judged by the casters it admits and by the shadow that results, and the
  command count is judged at the compaction step.
- Compaction alone is all that mostly unique geometry gains from grouping.
  Every consumer of a draw's identity has to move in the same piece of
  work.
- A bounding box that is not conservative hides a limb. A sample of poses
  does not bound every pose an author can write, which is why blended
  draws wait for a certified margin.
- Indexed draws carry history. A packed vertex layout once cut the loaded
  region by 34%, slowed the mesh and cascade passes, and was reverted. It
  returns only if indexed draws make the passes no slower.
- Indexed draws and glass do not combine freely. A vertex on a silhouette
  belongs to a front-facing triangle and a back-facing one, so a facing
  carried per vertex is wrong for one of them
  ([decision 14](#14-treat-a-nested-view-as-the-same-view-at-another-size)).
- Sessions do not map one to one onto residencies, in either direction. The
  boot world's sessions are views of its one residency
  (`WorldScreenBinder.FilmHomeViews`). Every seat and window presented in
  one other world share that world's one residency too. So work is counted
  per residency and per view, never per session. Only the decoding of rows
  that does not vary by view can be shared. The frustum, depth, phase and
  held history differ per view, and the late phase depends on the current
  depth.
- The strongest case against compaction is that recording is not most of
  the frame on the RTX 4070. The claim is about scale and code health. So
  compaction is measured apart from grouping.

**What we still have to find out.** What an empty command costs on each bar
device. What re-uploads 2,600 records at rest. Whether each bar device
supports the primitive id on each backend. Each measurement behind step 8.

**Evidence.** Code read for this page: the one command per candidate, the
far cascade's mask and two-phase test, the receiver mark built from the
camera's opaque hits, the filter radius sized from the blockers a receiver
finds, the far layer read through an earlier camera, the missing box for
skinned draws, the home sessions, the routed scene shared by an endpoint's
seats and windows, and the commit that packed the vertex attributes and was
reverted. From the visibility and virtual geometry plans: their step order,
and that blended draws wait for a margin. From the visibility area's map:
what the far mask marks against what the surface pass reads. From the
attack's readers, not reopened here: the two existing compactions, the
Vulkan backend's enabled features, and the zero render origin. Measured:
3.9 ms against 1.7 ms at the lobby, the far cascade's 23,125 to 4,138 draws
and 8.5 to 2.9 ms, and about 2,000 records re-uploaded. Outside sources,
confirmed: Logdahl's empty-draw timing, and Sakmary's four-texel
hierarchical test. Haar and Aaltonen's 250,000 objects in two indirect
calls is confirmed as a quotation, but that benchmark draws fixed clusters
of 64 vertices, so it supports the cluster experiment of step 8 and not the
grouping of whole meshes. The two later reviews read the same deck's share
of triangles culled differently (20 to 40% on one slide, 30 to 80% for
shadows on another), and its gain from triangle and cluster culling as
under 10% of geometry time. Partial: Bittner's 1.5 to 2 times on a game
scene, measured on a 2010 GPU with CPU occlusion queries. Burns and Hunt
report a smaller visibility buffer paying with several samples a pixel and
little or not at all with one, which is Puck's case, so nothing here
credits that paper with a gain on the Steam Deck. The published test of
indexed draws was opened by an attack reader and not by this page's
verification pass. The boxes for skinned draws rest on the virtual geometry
plan's own reasoning, which its posed audit has to prove.

**What it rests on.** One implementation: every step extends the existing
classification code (`SdfMeshOcclusion`). And a stable order for depth
ties.

### 12. Keep motion and placement as authored programs at zone scale

**The problem.** A zone of an MMO holds orders of magnitude more plants,
moving parts and terrain than the debug world's meadow. Four things in
today's code grow with that count, according to the foliage area's map and
the code:

- An authored swing is interpreted from the document every frame, with its
  driver looked up by name (decision 2).
- The phase of a gust of wind is a literal number computed per blade from
  its tile's origin. Every tile is therefore a separate prototype, and
  tiles cannot share one bake.
- A scatter is an authored rule that places many copies, and it can be
  written two ways. A loop in the document expands at compile time into one
  placement row per point, so the compiled document grows with the
  population. A distribution kept on one row compiles to a list of its
  occurrences in exact fixed-point positions (`CompiledPlacementStamp`).
  Drawing and collision both read that list. It is evaluated for the whole
  world at once, and a world may hold at most 32,768 colliders from
  placements (`WorldPlacementPolicy.MaxSolidPlacementColliders`). The two
  ways of writing do not give an occurrence the same identity.
- Terrain sectors bake at 2.8 m cells, not the 1.56 m they declare, because
  the sampling grid of a scoped creation spans a sphere around its origin.
  Relief is capped at 4 m, and nothing casts a shadow past 120 m.

**What we propose.** The engine speeds up the authored program and derives
its bounds. It writes no grass, wind or terrain code path in C#.

1. **One form for a motion program, compiled once** (option 12a). From that
   one compiled form come an evaluator on the CPU, a GPU kernel for the
   transform slots that no rule and no contact reads, one routine that
   derives the motion's bounds, and per-instance inputs. This is built in
   two stages: first the compiled form with its CPU evaluator, then the
   kernel.

   The kernel comes from the compiler that already turns shading programs
   into shader code. That compiler works from one table, which gives every
   operation its types, its cost and its shader text (`SdfShadeOps`). The
   table was built for shading a pixel. It has no state carried from one
   frame to the next and no transform as a result. So the first stage says
   what the table must gain for motion, or whether motion needs a table of
   its own on the same emitter. The visibility plan's matching step, which
   puts authored motion on the GPU, takes the same answer.
2. **Evaluate placement cell by cell, for collision and for drawing alike**
   (option 12b), after decision 11's instance table. Each placed thing is
   an *occurrence*. This extends the list of occurrences that exists, and
   it has four parts.
   - *The cell belongs to the placement, not to the renderer.* A cell is a
     box of a lattice in the compiled placement's own space. One function
     beside the existing stamp evaluates the occurrences of one cell into a
     cached fixed-point buffer. It serves two kinds of demand: a body's
     reach, for collision, and a view, for drawing. A residency only
     decides which cells a presenter keeps. This matters because the
     server has no residency. It does not reference the renderer's project
     at all. If cells were the renderer's, the server would have nothing
     to collide a body against, or would collide against a different
     population from the one drawn.
   - *An occurrence's identity comes from what its placement means.* Today
     it is the candidate's ordinal within its stamp, which survives
     dropped candidates. The identity becomes the sampler's own settings,
     its seed, the candidate's ordinal, and which mirror image it is. Two
     ways of writing the same scatter then give the same identities, in
     any order of generation. A candidate that was dropped still uses up
     its ordinal, so dropping one does not renumber the rest.
   - *What play changes is state in the simulation.* A plant harvested or
     a thing moved is kept as sparse authoritative state, keyed by that
     identity and applied over the stream. The mechanism for keyed state
     over placements exists (`WorldPlacementDeal`, `WorldPlacementResponse`).
     It keys by placement row. At zone scale it must key by occurrence. A
     moved occurrence is recorded in the cell it moved to, and suppressed
     in the cell it came from. If it were looked up only through its
     original cell, it would vanish whenever that cell was evicted.
   - *The cell buffers stay a cache* that can be dropped at any time.

   Which edits to a scatter keep an identity is stated with the design. An
   edit that changes only how many candidates survive keeps the identity of
   every occurrence that still exists. An edit to the sampler or its seed
   makes a new scatter, and state keyed to the old one is dropped. Before
   release that is ordinary churn.
3. **Terrain as source tiles of one relief series, with a far shadow map**
   (option 12c), in the terrain pass's order and after a terrain research
   round.

**Why.**

- *Motion.* It removes per-frame interpretation, and removes posing,
  journalling and re-uploading for every slot the kernel can own. It also
  removes the need for one prototype per tile. Outside practice supports the
  parts. Nanite Foliage, which is experimental, finds that bounds computed
  from skinning matrices beat those of a per-vertex offset. Crysis used a
  per-instance wind vector and stiffness with the phase taken from world
  position. Remedy calls per-vertex wobbling "painful if not impossible" for
  deep hierarchies.
- *Placement.* The compiled document stops growing with the population,
  and the collider ceiling becomes a limit on one cell and not on a world.
- *Terrain.* Sectors stop being a single tier, and relief stops being
  limited to the scale of a prop.
- Two of the owner's rulings point the same way. "Puck code over engine
  code" keeps behaviour in authored programs. "Ergonomic is optimal" means a
  tile placed sixteen times, or a scatter written once, costs what it means
  and not how it was written.

**What it costs or risks.**

- The CPU's and the GPU's floating-point results must agree closely enough
  for picking to work. A loose bound on a motion costs culling.
- Projecting each cell's occurrences onto the ground has to run as job
  work, off the frame thread.
- A stable identity alone is not enough for a harvested plant. If a cell is
  evicted and later derived again from the scatter alone, the plant grows
  back. That is why play changes live in the simulation. An identity seeded
  by the cell, or by the order of enumeration, would also change when the
  same placements are written through another template or when a cell is
  split. That is why identity comes from the placement's meaning.
- A design in which the presenter evaluates cells and the authority does
  not would let a body walk through a tree that is drawn, or into one that
  is not. That is why the evaluation has one function and two demands.
- Almost no terrain source was read. One was read through a text
  extraction and not checked: Lengyel's dissertation, whose transition
  cells join neighbours sampled at a ratio of two to one. It does not say
  what the right seam is for Puck's dual contouring, how tiles scale, or
  what a hybrid with a height field costs. A terrain research round comes
  before tiles are built at scale.

**What we still have to find out.** How closely CPU and GPU motion agree.
What the shading table must gain to express motion. The terrain round's
findings.

**Options we did not take.** Generating geometry every frame with work
graphs, generating blades procedurally, and wind as a per-vertex offset
(option 12d). The first does not run on the baseline hardware. The second is
a second representation of geometry. The third gives motion with no bound.

**Evidence.** Code read for this page: the compiled stamp and its statement
that graphics and contact read the same origins, the candidate ordinal that
survives dropped occurrences, the collider ceiling, the server project's
references, the keyed placement state, and the shading operation table.
From the foliage area's map: the facts about swings, gusts and terrain. From
the attack's readers, not reopened here: that the two ways of writing a
scatter give different identities today, and what the shading table lacks.
Outside sources, confirmed: Nanite Foliage, GPU Gems 3 chapter 16 on Crysis,
and Remedy. Unverified: van Muijden, that placement generated on the GPU and
copied back is deterministic and locally stable, and an erosion filter with
analytic derivatives that its own author calls inaccurate. The streaming
plan's seam rule supports the terrain tiles.

**What it rests on.** "Puck code over engine code" and "Ergonomic is
optimal" ([AGENTS.md](../../AGENTS.md#puck-is-pre-release)).

### 13. Shade through one path, and shrink the image history

**The problem.** Surface shading exists twice, and the per-pixel history
that antialiasing keeps is large.

- **Two shading paths.** Authored shading programs are the intended path.
  The switch that enables them is off by default and is described in the
  code as "the temporary programs-versus-layers measurement lever"
  (`src/Puck.World.Client/WorldRenderSettings.cs:34`). So the default frame
  shades with the older fixed layers.
- **The views pass is the largest pass.** It shades materials and lighting
  for each pixel and takes 3.7 ms at the lobby. Its kernel uses 255
  registers on the RTX 4070 under Vulkan, which limits how many pixels the
  GPU works on at once. Reading the probe volume for each pixel is about a
  third of the pass.
- **The temporal history is 188 bytes a pixel.** It holds
  `SdfWorldPackage.HistorySurfaceWords` (47) words for every pixel, in one
  copy for each frame in flight. 40 of the 47 are a window of the last
  eight jittered frames, five words a frame. Each frame the resolve writes
  every sample the window holds: the new one, and a copy of each older one.

**What we propose.**

1. **One shading path** (option 13a), after decision 5 has fixed the colour
   conversion, so that the migration's captures are judged only once. The
   author-shader plan keeps a matrix of the ways to run a shading program
   against each bar device. The work measures that matrix on the bar
   devices, adds shading in per-program bins only on a device that needs
   it, and then deletes the fixed layers. Whether to bin, and at what tile
   size, is chosen from three phases measured separately on the bar
   devices: building the lists, dispatching, and shading.

   The matrix's rule for passing has to change before it can choose
   anything. It passes a way of running programs only when no frame misses
   the device's bar. No device meets its bar before stage 4, so every
   column fails and the rule picks nothing. The rule is restated relative
   to the device: a way of running programs passes when it is no slower
   than the fixed layers on the same device and stop, beyond the fixed
   layers' own spread between runs. Two cases then follow.
   - *A specialized way passes.* The work also measures what a player
     meets when a program arrives that has no compiled kernel yet: how
     long the kernel takes to derive, what the surface shows meanwhile,
     and the cost of installing it. A missing kernel is derived and
     counted like any other cache miss. It is never refused. Until the
     kernel is installed the surface is shaded by the interpreter where
     the device's readings show that fits, and by the engine's neutral
     shading where it does not.
   - *The fixed layers and every specialized way all miss.* Then shading
     programs are not the cause. The per-phase readings say what is, and
     that is fixed first.

   The switch that is deleted is the temporary one between programs and
   layers. The authored default, `world.shade default`, stays, as the
   author-shader plan's migration says.
2. **Split the history** (option 13b), behind its own design. The
   eight-frame window moves to one buffer that is updated in place. Only the
   words that are reprojected stay per frame slot. The design has to answer
   one thing first. The resolve reads a pixel's history at the position the
   pixel came from and writes it at the position it is in now. Those are
   the same position only where nothing moved. A buffer updated in place
   cannot serve a pixel whose history comes from somewhere else, because
   another pixel in the same pass may be writing there. So the window is
   carried in place only where the two positions are shown to be equal,
   and a pixel that moved keeps separate storage or restarts.
3. **Three smaller changes, each on its own measurement:**
   - Store the per-pixel visibility record plane by plane (option 13c). The
     same change of layout already removed a bandwidth limit on the history
     surface.
   - Measure the probe lookup separately, then evaluate it once per view at
     its own rate (option 13d). The opaque surface looks the probes up at
     its own position and facing, and glass looks them up at the pane's. A
     pane against the sky has no opaque surface behind it to borrow from,
     and a pane in front of a wall is lit quite differently from the wall.
     So the mechanism is shared, and a result is reused only for a
     receiver at a compatible position and facing. Glass gets its own
     samples where it has none.
   - Run the exposure meter on a small fixed grid of samples (option 13e).
     It is dropped on a device where compiling the meter out saves less
     than a tenth of a millisecond there.

**Why.** One path removes a second implementation of surface shading and
is what terrain and weather materials need. The history split would save
about 370 MB at 1280 by 900. With a full, still window it would also stop
copying 35 of the 40 window words each frame. That is the most it can
save: a window that is still filling, or was reset by motion, holds fewer
samples and copies fewer. The planar record removes strided reads in the
resolve, the filter and the composite. A decoupled lookup would recover
some of the third of the views pass that the lookup takes, less whatever
glass still needs for itself. A meter grid stops the meter's cost growing
with resolution.

**What it costs or risks.**

- Nothing has been measured off the RTX 4070. List building, dispatch and
  shading have not been measured separately, either with one material on
  screen or with a mix.
- Restricting when authors' programs may be interpreted is a restriction
  on runtime authoring. It goes to the owner only when a device has an
  otherwise workable path and the readings show the interpreter alone
  breaks it, with the use that would be lost named.
- A history updated in place needs a barrier that spans frames, and a
  barrier between frames does not order two pixels inside one pass. It is
  designed and reviewed before it is built. No source covers an in-place
  history, and the one source on temporal supersampling the round listed
  was never read. It is read before the window's layout or sample count
  changes.
- The views pass reads every field of the visibility record, so a planar
  layout may not help it. Whichever layout a measurement favours is kept.
- A probe lookup at a lower rate shows stair-stepping at visibility edges.
  We found no system that shades below full rate without also filtering
  the result, though no source was checked for that, so none carries over
  directly.
- The exposure meter and a screen's glow are not one job. The meter takes
  percentiles of a view's image with weights toward the centre. The glow
  takes the mean colour of a picture a screen shows. A small bright patch
  and an evenly dim picture can have the same mean and very different
  percentiles. An earlier draft listed them as one reduction done twice.
  They can share a way of sampling and nothing more.

**What we still have to find out.** The three shading phases on each bar
device. How long a missing kernel takes to derive, and what the player
sees meanwhile. How often the history's two positions are equal on the
moving route. Each measurement behind step 3.

**Evidence.** Code read for this page: the switch's default and its
comment, the resolve's read at one position and write at another with five
words for each sample present, the two places that look the probes up, and
the author-shader plan's matrix, its rule and its migration. From the
shading area's map: the history's layout. From the second review, not
reopened here: that a screen's glow is a mean of its picture. Measured: the
views pass at 3.7 ms, its 255 registers and the lookup's third. Reasoned:
the 370 MB. Outside sources: Burns and Hunt, that building per-material
tile worklists cost "no more than 10 % of frame time" in their scenes,
confirmed. That figure covers building the lists only. The paper's remark
on tile size is partial: it concerns its separate 16 by 8 lighting kernel
and gives no threshold for a material dispatch or for Puck's 8 by 8 tiles.
Unverified: that a shipped title meters a reduced image, and that systems
which shade below full rate also filter.

**What it rests on.** One implementation and "Puck code over engine code".
The author-shader plan's own rule chooses the path, as restated above, and
that plan is updated in the change that takes this up.

### 14. Treat a nested view as the same view at another size

**The problem.** A view through a window, on a screen or through a pane of
glass costs more than it should, and it looks different from the same scene
seen directly.

- **Nested views lose shadows and ambient occlusion at any size.** Two
  identical constants turn ambient occlusion and every cascade off for all
  session views and camera views
  (`WorldSessionSceneEmitter.ReducedQuality`,
  `WorldScreenBinder.CameraViewQuality`). A portal that fills the screen is
  lit differently from the room behind it.
- **A sliver of window can cost a full image.** A window's image is sized
  by the projected bounds of its slab. If any corner is at or behind the
  camera's near plane, the image becomes full size
  (`src/Puck.World.Client/WorldPortalVisibility.cs:65`).
- **One pane of glass costs the whole image.** When a single glass box is
  in view, the glass passes clear `SdfWorldPackage.GlassSumWords` (16) words
  for every pixel and, according to the area's map, shade the whole pixel
  grid.
- **Under water the picture is a flat wash.** The water plan records that a
  camera inside a body of water shows a dim, even brown-green, because the
  glass pass has no case for an eye inside a medium. The game design calls
  for a dive.

**What we propose.** A nested view is the same view at another size. Its
minimum work comes from whoever consumes it. Its extent, update rate and
quality follow the pixels it presents, through one authored table. Its
shadows follow decision 6.

1. **A view that nothing reads costs nothing. A world's demand is its
   consumers' minimums first and its presented pixels second** (option 14a,
   with decision 2). Two existing parts do this between them, and each
   keeps its job. The view graph's host already measures every view's share
   of the display and turns it into an update rate
   (`WorldViewGraphHost.MeasureDisplayShares`, `SdfUpdateRateTiers.ViewHertz`).
   The selector already decides which meshes stay in GPU memory
   (`SdfResidencySelector`), and it knows nothing about views. So the host
   keeps the demand that comes from views. The selector receives, for each
   residency, the minimums its consumers are owed and the total: a
   predicted arrival, an indirect dependency such as a screen's light,
   picking, and the lighting trace. Work that several consumers share is
   counted once. Only then are optional quality and update rate divided by
   presented pixels. The minimums land in stage 2 with the capture order
   (decision 2). The division of optional work lands in stage 4.
2. **Fit each window's image to the part of its glass that shows** (option
   14b), as its own piece of work. A fit through the glass exists
   (`WorldWindowFrustumFit.TryFitWindow`). It fits the camera to the whole
   opening, and the image is sized by the glass's whole projected extent,
   not by the part on screen. This step fits to the visible part of the
   opening.

   One window's image can have several readers: two viewers who see
   different slivers of the same glass, picking, and next frame's
   reprojection. Shrinking the image for one of them gives the others the
   wrong pixels. So the crop, the projection and the mapping from the image
   back to the opening are published together with the image, and every
   reader uses them. Where two viewers' visible parts differ, they share
   one image that covers both, or each gets its own.
3. **Replace the two constants with an authored table, in two steps**
   (option 14c). Merging them into one constant would keep a visible choice
   in engine code for every author. So the first step deletes both and
   reads an authored table. The engine's default restricts nothing. The
   standard package authors what a nested view gives up at each presented
   size, and a world can override it. The second step comes after decision
   6 makes a view with fewer cascades degrade per pixel. It adds what the
   table needs to change quality without a visible pop.

   A threshold in the table has a cost of its own. Whether a view has
   shadows is part of its render graph. Crossing a threshold builds a
   replacement graph beside the one in use, and the view shows its last
   image until the replacement is ready. If the memory pool is full, the
   replacement is refused and the view keeps its old quality. A window
   already rebuilds at each step of its extent as the player walks up to
   it. So the table's thresholds sit on those existing steps, or carry a
   band that stops a view flipping back and forth at the edge.
4. **Let a still feed keep its last image** (option 14d). This starts from
   an explanation, not a new mechanism. The runtime can already let a
   view's last render stand (`RenderGraphRuntime.WithUnchanged`, the
   residency's last-rendered signature, and the temporal history's
   convergence rule). A camera that has converged on a still room may
   already stand today. So the work is to name why one particular still
   feed keeps rendering, change that cause, and count renders. An external
   frame, an exposure change and lighting that starts converging again must
   each still wake the view.
5. **Make glass cost what the visible glass covers** (option 14e), as its
   own piece of work: glass draws tested against opaque depth, a record of
   which tiles hold glass, shading in bins, and a clear of only those tiles.

   The clear needs to know which tiles to clear before anything is drawn
   into them. The glass pass adds each fragment into running sums, so a
   list of tiles learned from this frame's fragments arrives too late: old
   sums would already be mixed into new ones. The tiles to clear are
   therefore the ones the last use of that buffer wrote, kept as a list
   with the buffer. A buffer's first use clears all of it. The list for
   the camera's glass and the list for glass that casts shadows are kept
   apart.
6. **Two smaller glass changes after that:**
   - Skip atomic additions of zero, and stop resolving three positions for
     each fragment to learn which way a triangle faces (option 14f). Facing
     belongs to a triangle. It cannot be carried per vertex once vertices
     are shared, because a vertex on a silhouette belongs to one triangle
     that faces the camera and one that does not. So this is designed
     together with decision 11's indexed draws. The two candidates are the
     rasterizer's own facing, made to mean the same on both backends, or a
     value stored per triangle for the current pose. Today's code avoids
     the rasterizer's facing on purpose, because its sense follows each
     backend's conventions and not the mesh.
   - Shade the view correctly when the eye is inside a medium (option
     14g). The atmosphere already handles one case, a camera below a level
     surface. A body of water built as glass has none. Knowing that the
     eye is inside such a body is one field query for a view.
     It is not enough to colour the picture. Two rays from the same eye
     leave the water at different distances, and one may reach the bottom
     first. The glass pass already keeps, for each pixel, a signed sum of
     the distances to the glass faces a ray crosses. That sum is started
     with the medium the eye is in, and each ray is cut at its own surface
     or exit. This is the gap the water plan files, and it is built there.

**Reuse must respect who is watching, and still share.** A portal session
can be delivered full frames, a redacted projection or a replica, and
"Revocation changes the recipient view immediately"
([worlds](../architecture/worlds.md)). Two audiences at the same camera,
world and tick can be permitted different pixels. An earlier draft of this
page answered that by putting a counter for the recipient's view into the
key of everything reused. That is safe, and it shares nothing: every
viewer would get a probe volume of their own. The rule is narrower.

- **A reused result is keyed by the content of the inputs it read.** Two
  viewers who are delivered the same rows share what was derived from
  those rows. The disclosure layer already stores a delivered row once for
  each class of audience, by its content (`WorldDisclosureImages`). A
  probe volume reads only static rows, since moving bodies receive its
  light and cast none into it, so two viewers with different bodies and
  the same static rows share one volume.
- **A result that read rows only one recipient may see** carries that
  recipient's identity as well.
- **A changed key is not enough on its own.** It prevents a new hit. It
  does not take down an image that is already standing in for a view, and
  it does not stop a job that was submitted before the change from
  publishing afterwards. So the check is made twice more: when an image is
  bound to a reader, and when a finished job installs its result. That
  covers a nested reader, the temporal history and derived lighting. On a
  mismatch the reader shows a fallback it is permitted until a valid
  result is ready. The existing rule that a view may stand only while it
  is bound to the same resolved view (`SdfWorldPasses.Refresh`) is one of
  those checks, and it stays.

Decision 7's shared probe pool and step 4 here both carry this contract.
Pixels already delivered are outside it. No leak has been reproduced.

**Why.**

- *Demand by consumer.* It removes the hidden render and the filming of
  unseen sessions. The streaming plan already builds a residency's lighting
  when the selector demands its world: "First sight is too late a deadline".
  The owner has ruled that a screen lit by outside content keeps lighting
  its room live.
- *The authored table.* It removes a pop in lighting and antialiasing at
  every portal crossing, and a look that is built into the engine twice.
  The owner has ruled that a visible trade-off switches only where it is
  barely perceptible, by its size on screen.
- *A fitted window* stops a sliver costing a full image, and stops a window
  being under-sampled just before the player crosses it.
- *Glass by coverage* stops one pane costing a full-image clear and shade.

**What it costs or risks.**

- Presented pixels alone cannot tell three cases apart: an idle unseen
  world, a world that owes its first image behind an approaching portal,
  and a screen out of view whose glow lights a visible room. Picking and the
  lighting trace are consumers too. This is why minimums come first.
- A fitted rectangle that slides with the camera makes the image swim. It
  has to snap to a grid fixed on the glass. A pick through a fitted window
  must land on the same point as before the fit.
- A quality change that swaps the shading fragment rebuilds the view's
  render graph, holds the last image while it does, and can be refused for
  memory.
- A frozen scene is not enough to let a view stand. Lighting may still be
  converging, the antialiasing history may still be filling, or an external
  picture or its mapping may have changed.
- Glass by coverage adds one more atomic operation per fragment. A Vulkan
  device loss near these passes has not been explained yet. Repacking the
  glass sums risks their exactness.
- The waterline must not pop when the eye crosses it, and the colour must
  deepen with the length of water a ray passes through.

**What we still have to find out.** Why a named still feed renders today.
The cause of the Vulkan device loss.

**Options we did not take.** Weighted or moment-based transparency,
per-pixel lists, stencil portals, planar reflections and layered multi-view
rendering (option 14h). They approximate sums Puck already computes exactly,
or they add a second mechanism for nesting views.

**Evidence.** Code read for this page: the two constants, the window's
sizing rule from the glass's unclipped extent, the existing fit to the
whole opening, the 16-word glass clear, the glass fragment's three resolved
positions and its comment on the rasterizer's facing, the glass sums'
layout, the runtime's existing rule for a view that stands and how a
standing output resolves, the host's measure of display shares, and the
recipient view's type. From the water plan: the wash inside a body of
water. From the area's map: glass shading the whole grid. From the attack's
readers, not reopened here: that a quality change builds a replacement
graph which a full pool can refuse, and that delivered rows are stored by
content for each class of audience. Outside sources: occupancy-driven cost
in a shipped transparency system, unverified. No outside measurement
supports the fitted window, and no source covers the eye inside a medium.

**What it rests on.** The frame graph's decision that views are instances:
"Layered views return only if the counts call for them"
([register](../decisions/rendering.md#the-frame-graph-and-nesting)). The
owner's ruling that glass is a surface of its own, drawn after opaque
visibility. And [the water plan](water.md), which owns the eye inside a
medium.

### 15. Present frames the display has room for, and know when they showed

**The problem.** The engine cannot say when a frame reached the display.
It renders frames the display has no room for. Three smaller costs sit in
the same area.

- **A frame can be rendered and then thrown away.** On Vulkan, acquiring
  the next image and waiting on its completion signal both happen after
  produce, with a zero timeout. If the swap chain is full, the result is a
  skipped present (`src/Puck.Vulkan/VulkanFramePresenter.cs:16`):

```csharp
    private const ulong FrameAcquireTimeoutNanoseconds = 0;
```

- **The two backends pace frames in different ways**, and each layer keeps
  a different number of frames in flight.
- **The UI overlay's cost is pixels times records.** Its shader says so in
  its first line: "every pixel walks every submitted record"
  (`src/Puck.Overlays/Assets/Shaders/overlay-unified.frag.hlsl:1`). It costs
  about 0.9 ms with the benchmark panel showing.
- **An emulated machine's video is shown at the simulation's rate, not the
  machine's.** The video source declares that it updates once a tick
  (`src/Puck.Hosting/Graph/MachineVideoSourceUpload.cs:137-139`), so an
  upload is scheduled at most once for each completed tick, and the debug
  world ticks at 30 Hz. A write can also be refused or made to wait, and
  nothing counts which of the machine's frames were uploaded and which were
  lost. The machine's output does return the sequence number of the frame
  it wrote (`IMachineVideoOutput.WriteFrame`). The upload reads only
  whether that number is positive and advances a stamp of its own on every
  successful write (`MachineVideoSourceUpload.Write`). An unchanged frame
  is therefore uploaded again under a new stamp.
- **Screens light everything.** According to the area's map, every pixel
  walks every bound screen as a light with no range limit and no visibility
  test.
- **Only Windows has precise timing.** The interfaces for the display's
  timing and for a precise wait are implemented in the Windows platform
  project alone. On Linux the loop still paces itself, through the host's
  ordinary waits (`HostWaits.Until`), which wake less exactly.

**What we propose.**

1. **One record for every attempt to present** (option 15a). This is
   first. It holds the attempt, whether it was submitted, the display
   outcome where the host reports one (shown, dropped or unknown), the
   signal the outcome came from, and the queue depth. It extends what every
   presenter already reports (`PresentTimingSample`), and it is the
   benchmark's displayed-time stage with the outcome added. It reports
   three separate counts: frames whose production was avoided, submits
   accepted, and display drops observed.

   The record is keyed by a number the host gives each attempt, not by the
   driver's present id. An attempt that is skipped before it reaches the
   driver has no present id. The Vulkan presenter's id also starts again at
   1 whenever the swap chain is replaced, so two presents can share an id,
   and a late answer about the old chain could be credited to the new one.
   So the driver's id is stored beside the attempt, together with which
   swap chain it belonged to, and answers still outstanding for a replaced
   chain are closed as unknown.

   What counts as "displayed" is defined by what the surface offers. The
   Vulkan present-timing extension lets each surface say which stages of a
   present it can time, and requires only one of them. A displayed
   interval is counted only from a stage that marks the first pixel
   reaching the display. A surface that offers less has no display column.

   The record also carries decision 1's latency column: the time from the
   earliest input a frame used to its display or submit.
2. **One admission rule on both backends** (option 15b), once decision 2
   lets frames fit their period. On a valid surface, the host is admitted to
   a slot in the present queue before it renders. It then renders no frame
   that has no slot. The wait that does this exists on both backends
   (`ISwapChainPacing.TryWaitForFrame`). It gives up in several cases, and
   the host then paces itself by a timer and renders anyway. The rule is
   built by closing those cases on a valid surface, and by moving the
   Vulkan presenter's acquire of the next image ahead of produce. The
   latency column says whether waiting for a slot costs latency or saves
   it, and the rule is kept only if it does not cost.
3. **The overlay as bounded, clipped geometry blended in place, with text
   from an atlas texture** (option 15c). This is the UI plan's step 2, after
   that plan's measurement. The overlay keeps its own copy of its glyphs
   today, flattened into a buffer (`OverlayGlyphSdfPack`). It becomes a
   texture built by the same code that builds the world's text atlas. The
   two atlases stay separate, because they come from different sources.
4. **Show machine frames at the machine's rate** (option 15d). It starts
   with a trace on a scrolling cartridge, of the frames the machine made,
   the uploads that were asked for, the uploads that were accepted, and the
   frames that were shown. That says whether frames are lost and where.
   The change is then two things. The source stops declaring a tick
   cadence and declares the machine's own. And a short queue of complete
   frames, each stamped with the machine's own sequence number, stands
   between the machine and the display. That sequence decides every
   downstream refresh, together with a replacement or reset of the source.
5. **Screens join the one light selection** (option 15e). Screens already
   answer through the same interface as every other light. What they lack
   is selection and visibility. A screen's range comes from its luminance
   and area, and its visibility from the probes. A screen that goes from
   black to bright while out of view has to wake its source before the new
   light is budgeted, which is one of decision 14's minimums. This goes
   with decision 7's sizing of light reach.
6. **Display timing, a precision waiter and a display request on Linux**
   (option 15f), when a Steam Deck is available. It starts by measuring
   what the Deck does today: its cadence, how late its waits wake, and how
   quickly input and shutdown are served under load. The work is kept only
   for an improvement those readings show.

What a fixed-refresh display shows when the loop cannot hold its rate is a
setting, and the mechanism serves either choice: a steady whole divisor of
the refresh rate, or frames as they arrive
([authored looks](#looks-are-authored)).

**Why.**

- *The record.* An even loop interval can hide judder on the display, and
  the frame rate that players see is unexplained today. The Vulkan
  present-timing extension's own proposal says the older present-wait
  extension "lacks important details such as the latency of the present
  operation itself".
- *Admission.* It stops work being thrown away under backpressure, and
  stops two pacing mechanisms taking turns. Microsoft's guidance is that
  "Your rendering loop should wait for the swap chain to signal ... before
  it begins rendering every frame". NVIDIA recommends one or two more
  buffers than frames queued.
- *The overlay.* Its cost stops scaling with pixels times records, and a
  second full-resolution image goes away.
- *Machine cadence.* A scrolling cartridge is likely shown at half its rate
  today, and unchanged frames are uploaded.
- *Screens as lights.* It ends glow passing through walls and a per-pixel
  walk that grows with every screen bound.

**What it costs or risks.**

- A true display time on Vulkan needs an extension the bar devices may not
  have. Its absence is then reported as a capability, with a named
  follow-up. Where a compositor trace exists, the record is checked against
  it. A device with no display time can only be judged on what the host
  produced: the work of each frame inside the period, with skipped and
  dropped presents reported. A loop can hold 60 Hz while the display skips
  frames, so that reading does not show the bar was met on screen.
- Admission cannot promise display. A compositor may replace a submitted
  frame, and a surface may be lost. Three limits also have to be kept
  apart, each derived from its actual readers: the number of images, how far
  the CPU runs ahead, and how long a GPU reader keeps a resource. The vendor
  guidance cited above already separates buffers from queued frames. A wait
  for a window that never displays must stay bounded, and the host must keep
  serving ticks, uploads, input and shutdown during it.
- The overlay's diagnostic focus treatment reads the scene behind it, which
  blending in place has to allow for. A build that quietly dropped that
  treatment would be faster and would pass a frame-time check, so the
  overlay's check looks at the treatment itself.
- No display shows every machine frame exactly once at every rate. A 60 Hz
  machine repeats frames on a 120 Hz display and skips some on a 45 Hz one.
  So the contract is the latest eligible frame at a bounded age, with
  repeats and skips counted. Audio must follow the same clock.
- A 32-bit light mask does not stretch to hundreds of screens.
- Linux needs a research pass and the hardware.

**What we still have to find out.** Which present-timing and present-id
extensions each bar device's driver exposes, and which stages each surface
can time. Whether machine frames really are dropped on a running World.
Everything about Linux timing.

**Evidence.** Code read for this page: the zero acquire timeout, the
presenter's early returns and its id starting again on a new swap chain,
the presenters' shared timing sample, the shared wait and the cases in
which it gives up, the overlay's header, the video source's declared
cadence and its stamp, the unused frame sequence, the host's wait without
a precision waiter, and that only Windows implements the timing
interfaces. From the presentation area's map: screens walked as unbounded
lights. From the reviews, not reopened here: the overlay's flattened glyph
copy, that screens answer through the lights' interface, and that the
present-timing extension advertises its stages per surface. Measured: the
overlay's 0.9 ms. Outside sources, confirmed: the `VK_EXT_present_timing`
proposal's remark about present wait, Microsoft's swap chain guidance
(archived), and NVIDIA's buffer guidance. Partial: the present-timing
proposal relies on a second present-id extension, and nobody has checked
whether the bar devices' drivers expose either. An attack reader found the
extension's reference page describing it as ratified. This page read only
its proposal. Unverified: Chlumsky's distance-field text (one bilinear
sample and a median). The machine cadence claim was read from the code and
has not been confirmed on a running World. No verified source supports
screens as selected lights, and no source was read for Linux.

**What it rests on.** "Puck plays nice with its host": waits go through the
host's primitives, and polling is never the steady state.
[The UI decision](../decisions/ui.md#drawing-screens-and-mods). And the
machines decision that neither visibility nor presentation rate controls how
a machine advances. Which frame is shown is a presentation matter and never
a simulation one.

## Work the areas do more than once

Each row is one job that the code does in several places today. The decision
in the last column leaves one implementation, except where the row says the
places differ for a reason.

| The job | Where it is done today | Decision |
|---|---|---|
| Choosing a reduced quality for a nested view | Two identical constants, in two projects | 14 (option 14c) |
| Rendering the sky map | A GPU kernel, a CPU render that decides whether to refresh, and a CPU render for each lighting capture | 7 (option 7f) |
| Deciding what a pass can show | The GPU kernel. A CPU copy of the test (`SdfMeshCull`, `SdfMeshBox`) that glass and update rates still read. A projection onto the light's plane that is written out five times in shader code | 11 (option 11f) |
| Whether the sun reaches a point | Cascades in some views and a probe bit in others, chosen per view | 6 (option 6c) |
| Choosing detail by size on screen | Three constants, and four places that compute size on screen: a group's tier, a mesh's level, a body's update rate and a viewport's pixels per metre | 8 (option 8b) |
| Budgeting background work | A budget in pass pixels, a share of GPU wall time, relight credits per frame, a fixed cost per bake sample, and a CPU share per service | 4 (option 4g) |
| Reading how loaded the GPU frame is | Dynamic resolution's load source, and the headroom rule an earlier draft of this page proposed beside it | 4 (the first half of option 4g): one reading |
| Compacting a list on the GPU | The probe client's compaction, which runs in series, and the shading bins' scatter, which keeps no order | 11 (option 11a): one ordered scan and scatter, written as a shared header |
| Counting frames in flight | Five different numbers: the render graph's 3, the tables' ring of 2, the Vulkan presenter's 2, the Direct3D 12 swap chain's 3 and the background service's 4 slots. These are not one number held five times. Each counts a different thing with its own readers: images, how far the CPU runs ahead, and how long a GPU reader keeps a resource. What they can share is the way a slot is admitted and released. Each capacity stays derived from its own readers | 15 (option 15b), 4 (option 4e) |
| Knowing a GPU submission finished | Completion signals shared with a batch's owner, beside the device's own timeline | 4 (option 4e) |
| Running CPU work on other threads | The bake's own pool, the lighting's loops, thread-pool tasks and the job pool | The job system's pool (J1) |
| Placing keyed ranges in a buffer | One allocator for the probe scene and another for the mesh region | The lighting work packet |
| What a colour value means | Linear for shading, decoded for keyed blends, encoded for image sources and the display encode | 5 (option 5b) |
| A tone curve | The root graph's filmic constants, the cel style's curve and the cel program's shoulder | 5, as authored data |
| Applying a view's bloom | The root's `place` pass for a placed view, and the view's own `bloom-apply` pass for a filmed one | 5 (option 5b) |
| Shading a surface | Authored programs and fixed layers | 13 (option 13a) |
| A motion's value and its bound | An interpreter that looks drivers up by name, the audit's fixed-point pose, a measured reach and a measured speed | 12 (option 12a) |
| Storing baked meshes | One store per backend | 10 (option 10f) |
| Reducing an image to a statistic | The composite's histogram for the exposure meter, and the grid that computes a screen's glow. These are two different statistics of two different images, and stay two. They can share a way of sampling | 13 (option 13e) |

### Out-of-date text found while reading

Each of these is fixed by the step that touches it or by the next
documentation sweep.

- The rendering skill and the mesh-bake reference give the mesh region's
  size as 1.75 GB.
- The mesh-bake reference and the handbook say the levels keep 11% and 6%
  of the triangles.
- The kernels reference says the glass clear zeroes eight words a pixel.
  `SdfWorldPackage.GlassSumWords` is 16.
- The illumination plan's table of rejected options says the cascades take
  48 MiB.
- The summary on `SdfMeshIrradiance` still says the views pass multiplies
  the sky's shares by the live sky.
- The streaming plan cites a spin wait that the code no longer has.
- The lawn's page says each static row bakes on its own.
- Two open items are out of date: the one about the presentation view's
  last cut, and the one about the occlusion test's identity check
  ([items 1 and 8](#where-the-research-disagreed-and-what-we-did-about-it)).

The header of the bake's work packet, which says the bake is CPU-bound, is
unsettled and not out of date. The critical-path run decides it.

## Combinations only Puck can make

Other engines start from meshes. Puck starts from an authored distance
field, identifies every derived product by its inputs, treats behaviour as
authored programs, and composes worlds into one frame. Seven combinations
follow from that. Each has a way to fail.

- **One chain of meshes, measured against the field.** The floor is a tier
  of the same shape, built by the one mesher and identified by its inputs.
  So a missing mesh, a far cascade's casters and a distant copy all draw it,
  with no authored stand-in and no second derivation. The lighting traces
  the same floor wherever it passes the lighting check. Every rung has one
  reference, the authored field, which an engine that starts from meshes
  does not have. *It fails* where a floor contours a thin part away to
  nothing, where a floor covers a wall but loses the recess the light needs,
  and where a switch between tiers shows on thin geometry.
- **The field is still there at bake time.** A cell that cannot hold its
  features can be sampled again. Finding where the surface crosses an edge
  is a loop a kernel can finish on its own. A part with a closed-form
  surface can be converted exactly, because its program says what it is.
  Error is measured against the field. *It fails* where the CPU evaluator
  and the GPU disagree below their resolution, and every kernel edit forces
  a rebake.
- **Probe hits that are exact and independent of the view.** Surface
  entries keyed from exact hits are valid from any camera, so a teleport or
  a portal finds a place lit wherever the pool already holds its entries.
  Which lights reach a surface is geometry, made stale only by what
  changed. A drain is a function of its inputs. *It fails* in that the
  sharing ratio is unmeasured, a place with no entries yet still has to be
  traced and lit from nothing, and a per-source sky is exact only for a
  uniform change.
- **Consumers' minimums first, then presented pixels.** Portals and nested
  views are worlds. So before the frame is built, the selector knows who
  consumes each residency and what share of the picture each has. Minimums
  cover what is owed before a pixel shows. Extent, update rate, quality and
  budgets then follow one number. *It fails* if pixel share alone is used,
  which starves a world that owes a first image or lights a room from out
  of view. Measured prices can also oscillate.
- **Programs whose bounds can be derived.** Motion, placement and shading
  are declarative. A moving part's swept box, a cell's population and a
  pixel's cost are therefore derived and never guessed. *It fails* in that
  creations mixing program kinds stay on the CPU, and a loose bound costs
  culling.
- **Every published result has an identity.** A machine frame's sequence
  number, a view's packed signature, and a sky's packed inputs with its
  acquired pictures decide every downstream refresh. A published result is
  also identified by the content of the delivered rows it read, so viewers
  who are delivered the same rows share it and a viewer who is delivered
  less does not. *It fails* where "unchanged" is wrong, which shows as a
  frozen picture, and where reuse ignores the recipient, which shows what
  was withdrawn.
- **A receiver mask taken from the visibility record.** One structure
  serves three uses: culling shadow casters, the minimum update rate that
  takes occlusion into account, and the measurement that decides whether
  shadow storage can be sparse. *It fails* on a surface that came into view
  after the mask was built, which is why such a surface takes the probes'
  answer until the shadow map is drawn again.

## The build order

This is the round's order by dependency, as three reviews corrected it.
Each stage's steps are filed in the plan its decision names, and
[open items](open-items.md#next) ranks them among everything else.

### How every check is read

Six rules apply to every check below.

- **How the frame-rate bar is read.** The bar is stated once, here, and
  every check that claims it uses this reading.
  - *What is read.* The slowest frame's work: the loop iteration less the
    time the pacer waited (`HostFrameRecording` keeps both). Where the
    device reports display outcomes, no present is skipped or dropped.
  - *At what quality.* The `world.quality high` preset, with the render
    scale at native and dynamic resolution off. The benchmark applies the
    preset before the first chapter, outside the timed span.
  - *At what window size.* The benchmark's default window of 1280 by 720
    on the two desktop devices, and the panel's own size on the Steam
    Deck. A second reading at 1920 by 1080 is taken on the desktops. The
    size the bar is finally read at is
    [the owner's call](#the-owners-calls).
  - *With nothing quietly reduced.* The detail bias is zero: under memory
    pressure the bake can coarsen every mesh without saying so in the
    picture (`SdfMeshTier.Biased`). No refusal is printed beside the
    reading, such as a render graph that could not be replaced for want of
    memory.
  - *Under recorded conditions.* On Direct3D 12 and on a windowed Vulkan
    boot, in real time on the benchmark's route, under the recipe. The
    run records its quality settings, GPU, driver, display state, whether
    the mesh store started empty, and the machine's clock and power state.
    Two runs are compared only when those match.
- **How a change is judged.** Each landing is compared with the build it
  started from. Both are frozen and run in the same quiet session
  (`puck measure`), and the difference is judged against the base's own
  spread between runs. A single slowest frame is not enough either way: one
  interruption can fail a faster build, and one unchanged cold spike can
  hide a slowdown on every other frame. So a check reads the slowest frame
  with its cause, how many frames missed the period and by how much, and
  the counted work, over repeated runs. A chapter that was disturbed, went
  off course or did not finish fails the check. It is not left out.
- **Needs are stated per item.** A stage is a group of items, not a gate.
  An item waits only for what its own needs name. An item whose criterion
  is a fixed figure, and misses it, does not hold the items after it: the
  entry under [what would change the answer](#what-would-change-the-answer)
  says what happens instead.
- **No dependence on stronger hardware.** No stage relies on a code path
  that only stronger hardware runs.
- **A claim that can be shown false.** From stage 1 on, each check states
  the gain its change claims and what result would show the claim false, on
  the same moving route. The work is counted beside the time: instances
  visible and processed, draws and dispatches, bytes held, and the slowest
  frame. Naming the cause of a slow frame is a measurement. It is required,
  and it closes nothing on its own.
- **Where sharing is claimed, the arrangement of worlds is stated.** Every
  world of the debug world admits every viewer at full disclosure, so a
  check run there shares as much as can be shared. A check that claims
  work is shared between viewers is also read with one window onto a world
  that gives the viewer less than a full replica, in the same process. A
  world served by another process is left to the federation work, because
  a remote world's screens show nothing yet.

"The route" below always means the benchmark's route. Where an earlier
draft said "the tour", it meant the same thing.

| Stage | In one line | Needs first |
|---|---|---|
| [0](#stage-0-measure) | Build the route and the instruments, and take the missing measurements. Change nothing a player sees | A quiet measurement session, an RTX 2060, and a Steam Deck whose windowed path has been brought up |
| [1](#stage-1-fix-what-the-player-sees-everywhere) | Fix what the player sees everywhere: colour, shadow roles, lighting lag, contour defects, nested-view quality | The lead machine's part of stage 0. An HDR display for the colour check |
| [2](#stage-2-the-frame-thread-and-the-round-trips) | Shorten the frame thread's work, compact the draw list and remove the round trips | Stage 0's breakdowns. Stage 1's headroom rule and its closing bake run |
| [3](#stage-3-one-instance-table-and-one-chain-of-meshes) | One instance table and one chain of meshes | Stage 2's compaction and instance updates. The shader matrix measured on the bar devices |
| [4](#stage-4-scale) | Scale: populations, far shadows, nested views, presentation | Stage 3's table and chain. The terrain research round. A Steam Deck. An adjacency authored between two debug-world worlds |

### Stage 0: measure

**What lands.**

- The benchmark reads the bar as stated above: it applies the high preset,
  and records the conditions two runs must share.
- The route. It has three chapters today, all named "baseline". It gains
  the chapters later checks name: Valley, Meadow, Entrance and Night. The
  benchmark plan already proposes them. Night is reached in a way a route
  can run. A capture can be taken at a named tick of a chapter. Each
  chapter's program is frozen before its first compared reading, because
  the benchmark treats a chapter whose program changed as a new workload.
- The measurements in decision 1's table, and each candidate's own first
  measurement.
- Beside the stage, the Steam Deck's bring-up: a first windowed boot on the
  device, the benchmark and the quiet session running there, a way for a
  Deck session to get its meshes, and the least display timing a valid
  reading needs.
- The device session's first task on the RTX 2060 is the gap between its
  two backends that open items already holds unexplained. It was read with
  no record of the GPU's clock state, which is now a recorded condition.

No behaviour changes.

**It needs.** A quiet measurement session. An RTX 2060 and a Steam Deck for
the device rows. The research owed on display timing, read from each
device, before the present record is designed
([research still owed](#research-still-owed)).

The stage closes device by device. The lead machine's rows are enough for
stages 1 and 2 to start.

**It is done when:**

- A `puck benchmark` run applies the high preset before its first chapter
  and reports the conditions it ran under. Two runs that differ in a
  condition are refused as a comparison.
- The route plays every chapter a later check names, on both backends, and
  the windowed benchmark run that open items still owes has been taken.
- Every row of decision 1's table has a reading in the report, on the
  devices its row names. A row without one names what blocks it. Produce's
  parts sum to within 5% of the `produce-work` figure that `host.frames`
  reports.
- Every attempt to present has a record with its outcome and the signal it
  came from, and the report states the share whose outcome is unknown. On
  Windows the record agrees with a compositor trace of the same presents,
  as the benchmark's displayed-time stage requires. A device and backend
  pair that provides no display time says so in the report, with the
  follow-up that would supply it. The report then either explains why the
  frame rate reads as it does, or says that it cannot yet.
- Each bar device has its table: the slowest frame of each chapter against
  the bar, by part, at both window sizes on the desktops.
- The time from an edit to seeing it is reported for a `.puck` edit, a
  shader edit and a C# edit, by phase.
- The night-switch reproduction and the meadow report shadow queries issued
  and pairs of hit and light tested, and the delay before the lighting's
  version moves.
- The Steam Deck has booted the debug world windowed and played the route.

### Stage 1: fix what the player sees everywhere

**What lands.**

One sequence, because three of its pieces edit the same code: the selector
that chooses a view's bloom, shadow and glass passes.

1. Decision 5's conversion in each view's own graph, with the sharpen
   moved into it, the filmic constants moved into authored data, the two
   colour types, and the debug world and standard package retuned in the
   same merge.
2. Decision 6's admission of lights, cascade roles and per-pixel probe
   fallback, as one piece of work, built against the cascade set's data
   model.
3. Nested views' quality as an authored table (decision 14, option 14c's
   first step).

Beside that sequence, and independent of it:

- Decision 4's headroom rule on the one reading of the frame's load. Then
  bake jobs cut to the allowance, and the per-device upload rate. Then
  several probe jobs in flight, stale relights sharing the worker, and the
  grant per second.
- Decision 10's keys by stage, its contour rule and its removal of
  redundant triangles.
- The prototype of the metre-sized shadow filter, behind its switch.

**It needs.** The lead machine's readings from stage 0: the split of GPU
time and the per-frame GPU span for the headroom rule, the whole-world mesh
audit for the contour rule, the edit-wait readings for the keys by stage,
and the route's chapters. An HDR display for the colour check. The unmerged
lighting branch merged and run in a World for the first time.

**It is done when:**

- **Colour conversion.** A grey card reads 118/255, within 3 codes, under
  its manual exposure value, through every path a view takes: a single
  world, a split view, a filmed view shown on a screen, a view beside a
  pane, a fully framed screen, a sharpened view with bloom, a camera read
  both as scene light and directly, and a window from a dim room onto a
  bright world, where the two exposures differ. Each display output the
  device exposes is read back in its own format: SDR in both of its cases,
  scRGB and HDR10. On each, neutral and coloured patches authored at N
  nits show N times the exposure times the display's white level, with the
  tone transform at none, inside a stated tolerance. One swatch for each
  kind of scene colour reads correctly: a surface, the sky, fog, a light
  and a colour keyed over time. A letterbox colour and a HUD colour do not
  move. A bright edge in motion shows no new shimmer on SDR or HDR. A graph
  that joins two kinds of image without the conversion fails to compose.
  The gain claimed is one meaning for a pixel value. Any path where the
  card reads differently shows the claim false.
- **The retuned world.** Every chapter of the route meets its reference in
  motion, and the gallery's exhibits still read as their plaques say. The
  judge is not the lane that tuned them. The lamps' ranges are no longer
  than before.
- **Shadow roles.** All of this is read at the high preset, where two
  lights cast shadows, and the first check is shown failing on the base
  build. At noon, `world.counters gpu pass sdf.world$shadow-second` shows
  no draws. Through a continuous exchange of sun and moon, run forward and
  reversed, no frame is without cascades. Through a crossing where both
  lights stay lit and swap which is brighter, a far receiver's shadow
  changes without a jump in either direction, and the brighter light ends
  with the near and far cascades. A capture shows a shadow beyond 40 m
  while the sun's slot empties. Each end of the exchange matches a cold
  jump to it. A capture across the 120 m edge of coverage shows no hard
  line and no sunlit interior. A capture in the frames just after a camera
  cut shows the unfilled area dark and not sunlit. A second view far from
  the first still gets an answer.
- **Lighting lag, inside the headroom.** The lighting is judged by each
  probe level's oldest age against that level's bound, and by the count of
  levels behind, which is the engine's own test. It holds on the
  night-switch reproduction, with traces owed and with none, and on the
  route's moving chapters. It holds at the display's rate and at half of
  it, with every job slot full and with a lighting change in the middle of
  a batch, on both backends. It is read where the GPU limits the frame. If
  no stop does on the lead machine, a forced load makes one. In the same
  run the root frame is no slower than the base beyond the base's spread.
  An age that falls only by taking frame time shows the claim false. Three
  more legs: with dynamic resolution on (`world.render-scale auto`), the
  two controllers do not fight. Under a load that fills the period, with
  every slot already full, frames that miss behind a background job and
  lighting that is overdue are counted apart. And in play no background
  job measures longer than one frame's allowance. The lawn stops being
  metered as day.
- **Keys by stage.** An edit to the Land stage's code rebakes with no GPU
  sampling dispatched. The edit-wait reading for that edit falls.
- **Contour.** The stored-mesh audit over the whole world reports no class
  of defect worse than before, on both backends, and no wrongly facing
  triangle on the fourteen blend exhibits. Every audited group is
  non-empty, and its `open` and `uncovered` measures are no higher than
  before. For each group that failed, the report says whether its cell was
  ever flagged for refinement. A blade part has about a dozen triangles.
  The blend seams, and the rim of the Moth in motion, are captured from
  the sides that failed. The bake's time is reported beside each fix.
- **Nested quality.** One lit exhibit seen through a full-size portal
  matches the root view from the same pose. A camera's feed matches a root
  view placed at the camera's pose. An author's override of the table
  changes the nested views alone. `puck search` finds neither constant. A
  walk across each of the table's thresholds, on both backends, is read
  for its slowest frame, its peak GPU memory and any refused replacement.
- **The shadow filter's prototype** stays inside its stated bound against
  the brute-force filter, in a burst of presented frames, on all four
  blockers.
- **The ranking bake.** One cold bake under the recipe, taken at the close
  of the stage, gives the critical path that ranks decision 10's two speed
  fixes, and the cold bake's figure.
- The Valley, Entrance and Night chapters each show these in a burst of
  presented frames.

### Stage 2: the frame thread and the round trips

**What lands.**

- Decision 2's capture order, its upkeep step, the minimums for unseen
  worlds and state keyed by view. The cuts to field queries and to
  allocation. The cheap freshness check only if the breakdown calls for it.
- The job system's bounded waits (C0), then its pool with an owned fork
  (J1), then decision 3's tick partitions (T3, T2) and the tick's serial
  fixes in the order its breakdown gives.
- Decision 11's first three steps: reduced rates for far placements,
  uploads only for instances that changed, and ordered compaction.
- Decision 4's work list on the GPU and its early hand-off.
- The bake's first speed fix, whichever the critical path ranks first:
  sampling in one round trip per stage, built from one root-search rule, or
  the Land stage on the pool.
- Decision 7's single sky, per-source storage for edits that keep a
  light's reach, and the rescale of probes a relight has not reached.
- `transmit-identity`, then decision 6's metre-sized filter together with
  `transmit-plane`.
- Decision 11's receiver masks as a conservative union, and boxes for
  rigidly skinned draws.

**It needs.** Stage 0's breakdowns of produce and of the tick. Stage 1's
headroom rule, before the GPU work list raises the background work's use of
its share, and stage 1's closing bake run. A device session for the
readings on which the transmission plan closes `transmit-identity`.

Some of this is serial. The bounded waits, the pool and the partitions
follow one another. `transmit-identity` comes before the metre-sized filter,
which is one piece of work with `transmit-plane`. The GPU work list lands
before the lighting's CPU loops move onto the pool, because both rewrite
the same code. The early hand-off lands after the rework of waits. The
bake's chunk scheduling lands after the pool. The instance updates land
before compaction. The capture changes and the GPU work list are
independent of each other.

**It is done when:**

- **Capture.** Captures counted by cause show none that no footprint, owed
  upload or minimum read. A scratch World holding extra unseen residencies
  shows the same capture count, and a begin-frame time that grows only by
  the upkeep step. A camera turn that reveals two levels of windows shows
  both in that frame. A window showing its fallback image is a counted
  miss, and is allowed only for a camera whose pose is not yet known. With
  the hidden arrival render gone, the destination of an approaching portal
  still has its first image and its lighting before its first pixel shows,
  and a changing screen that is out of view still moves the light of a
  visible room. An unseen residency still gives memory back when asked,
  still retires what it released, and has a shader reload applied before
  it is next seen. A viewer whose right to see a world is withdrawn while
  their window's picture stands does not see that picture in the next
  frame delivered. No garbage collection pauses the steady loop. A capture
  count that the reordering leaves unchanged shows the claim false.
- **The cheap freshness check, if it was built.** The audit switch reads
  zero disagreements over the route, and over a test world that puts a
  moving body, a keyed light, a flowing surface, fog, a change of quality
  setting and a shader reload behind a window whose picture is standing.
- **The pool before the partitions.** A console verb issued while a tick's
  join is overdue is answered within one iteration. Through a cold bake, the
  process's threads are counted as pool workers, the frame and control
  threads, and dedicated threads, each against the job system plan's
  figure. The tick is reported in milliseconds by phase for each chapter,
  before and after. The report also states whether the tick and produce
  each fit the period while their sum does not, which is the reading that
  would reopen a tick on its own thread.
- **The draw list.** In every raster pass, commands executed equal the
  draws that survive. Records staged in a frame fall to the slots that
  moved.
- **Background GPU work.** With a small grant, a probe facing a wall that
  many lamps light stays inside the cap and resumes in the next dispatch,
  and the most work in one dispatch is reported. Drains run with different
  batch sizes reach the same result. With the early hand-off, a job's
  answer is available one step sooner, and the extra submit's cost is
  reported beside it. A validation run of both backends completes clean.
- **Lighting by what changed.** A change of only a directional light's
  colour or brightness, or of the sky's gain, relights no probe and matches
  a from-scratch drain within `ConvergedShare`. The same holds for the
  other cases decision 7 lists: one of two lamps changed, a light taken
  from zero to on, a receiver at the old range boundary of a brightened
  lamp, a sky gain that is not a power of two with a rounding grid set,
  and two coloured lamps with an emitting object and a wall lit only by
  bounce. A panoramic sky picture that moves under unchanged sky
  parameters refreshes the sky. An unchanged procedural sky under a moving
  camera does not.
- **The bake.** `puck bench startup --bake` under `puck lease quiet` reads
  about a minute or less on each backend, from that backend's own empty
  store, with the first-ranked speed fix landed. If the sampling fix is the
  one that lands, a group takes about four round trips. Its roots pass
  bracket, residual and thin-feature checks against the authored field on
  both backends. While the player turns into a new place during a bake,
  the first visible surface arrives no later than before.
- **The shadow filter.** A walk through the boundary between cascades shows
  one soft-edge width on either side and no seam. A deep blocker directly
  above and a blocker displaced to the side both shadow as the brute-force
  filter does. A thin occluder beside a back face lit by wrapped lighting
  keeps a correct shadow in motion on both backends.
- **Caster culling.** With culling on and off, moving captures agree in six
  cases: backlit glass against the sky, across the blend band, on the first
  frame after a cut, when a foreground object is removed while a far
  shadow map is held, when the camera moves sideways during a hold, and
  when a new blocker widens a soft edge where the map was empty. The step
  is judged by the casters it admits and the shadow that results. A
  rigidly skinned draw is culled when it cannot show and still shadows a
  visible receiver.
- The slowest frame of every chapter is compared with the stage's starting
  build on the lead machine, under the rule above. The stage's closing
  build is frozen, and stage 3 is compared with it.

### Stage 3: one instance table and one chain of meshes

**What lands.**

- Commands grouped by geometry and level, the split draw record, and the
  render origin's contract, in one change of format.
- The indexed-draw experiment.
- One read of the instance rows per residency, and the glass list from the
  kernel. The clear of glass shadow storage by tile.
- Decision 8's single function for size on screen, and its chain measured
  against the field.
- The streaming plan's backing, floor, eviction and keeping of what was
  observed in use, and the lighting's own class of demand.
- Decision 7's shared surface lighting and visibility bits, and the count
  that decides coarse-to-fine convergence.
- The bake's second speed fix, if a measurement shows it pays.
- One mesh store, judged separately from the cold bake's figure.
- The one shading path.
- The rest of the eye-centred cascade memo.

**It needs.** Stage 2's instance updates and compaction, for the grouping.
The shader matrix measured on the bar devices, for the shading path. Stage
2's frozen closing build, for the comparison.

**It is done when:**

- **The table.** The number of commands is close to the number of visible
  pairs of geometry and level, with picking, motion and portal straddles
  intact. The route's counts of visible and processed instances and of
  dispatches, read before and after, show commands falling with what
  survives. Two views of one other world that face different ways, and a
  view of a second other world, read two residencies' rows once each and
  classify three views. The same holds with one window onto a world that
  gives its viewer less than a full replica.
- **Indexed draws.** The experiment is reported pass by pass on each bar
  device and backend. It becomes the baseline's format only if every one
  supports it and none is slower.
- **The chain.** Walking up to one tree changes no distant tree's
  triangles. A large copy far away and a small copy close by each draw
  their own rung. Thin contours and offset contours hold through each
  change of tier, in camera views and in shadow views. A rung whose
  recorded deviation fails the consumer's budget is not taken. A group
  with two separate parts, one of which a coarse rung loses, is recorded
  as not qualified.
- **Residency.** A burst of cold arrivals shows no frame with a missing
  surface. A cut to a place whose meshes are not stored shows the last
  complete scene or the fallback image until the place's floors and
  lighting stand, and floors not ready, failed and evicted are counted
  apart. The same holds when a mesh cannot be derived at all. Through a
  real reduction of the budget, with a block known to be releasable, a
  release is observed and the bytes held fall by it once the last reader
  retires. With something deliberately in the way, the report accounts
  for the bytes that stay. Both run with a shared block and with the
  forced contiguous fallback. Counts of evictions and tier changes are
  read as diagnostics only.
- **The lighting's tier.** At a lit exhibit, on both sides of a thin
  partition and in a lit recess, the light traced against the lighting's
  mesh matches the light traced against full detail, inside a stated
  tolerance. A partition built to fail the lighting check does fail it.
  With the eye moving and probe demand held, a wave of tier changes
  rebuilds no trace scene. Trace rebuilds are counted separately from
  lighting error.
- **One shading path.** The temporary switch between programs and layers
  is gone, `world.shade default` remains, and the migrated route matches
  its references in motion. List building, dispatch and shading are
  measured separately, with one material and with a mix, on each bar
  device. So is the arrival of a program with no compiled kernel.
- **One store.** Each backend's cold bake is timed from its own empty
  store, with floors on, and still reads about a minute or less. Reuse
  across backends is shown in both directions by audit, materials, seams
  and captures in motion, on each bar device.
- The slowest frame of every chapter is measured on all three bar devices
  and compared with stage 2's frozen build, under the rule above. Each
  device's table from stage 0 is updated. On the RTX 2060 and the Steam
  Deck the lighting's age, the share it was granted and the rates it
  achieved are read beside the frame.

### Stage 4: scale

**What lands.**

- Decision 12's motion kernel, placement evaluated cell by cell with its
  identity and authoritative changes, and terrain tiles. Ground cover
  thinned with distance.
- Decision 6's far cascade entries, and the rest of the transmission plan.
- Decision 14's division of optional work by presented pixels, fitted
  windows, quality by presented size, standing feeds, glass by coverage,
  glass facing and the eye inside a medium.
- One lighting pool and one priced GPU budget, shared by the content of
  what each viewer is delivered. Emitting surfaces lit as areas, and
  screens as selected lights.
- Decision 13's history split and its three smaller changes.
- Decision 11's smaller per-pass savings.
- Decision 15's admission rule, overlay, machine cadence and Linux timing.

**It needs.** Stage 3's table and chain. The terrain research round, for
the tiles. A Steam Deck, for the Linux timing work. An adjacency authored
between two worlds of the debug world, for the seam check: none exists
today.

These pieces are independent, except where one names a need above.

**It is done when:**

- **Motion at scale.** A scratch meadow with four times the tiles, with the
  added tiles populated and on the route, shows a pose-packing time that
  follows the animated instances in view and not the tiles placed. Both
  counts are in the report.
- **Populations.** The checks close on a named place of the debug world. A
  body far from any view still collides with a scattered tree. A cell's
  bytes are the same from a headless World and a presenting one. The
  collider ceiling holds per cell. A harvested occurrence stays harvested
  through eviction, re-entry and a replay, and after the scatter's density
  is raised and the world reloaded. A moved occurrence is picked and
  collided with while only the cell it moved to is loaded. Two ways of
  writing one scatter give the same identities. Thinned ground cover does
  not pop in a presented walk, in the camera's view or in a shadow. A belt
  of sixteen times the area is booted as a diagnostic of bake time and
  memory.
- **Seams between worlds.** A ridge in one world casts its shadow across
  an adjacency into the next, and a body is handed across it in motion.
- At `valley-rim`, ridges cast shadows and the frame bar holds under a
  moving sun.
- **Optional work.** A nested world that is deliberately overloaded leaves
  the main world's lighting age unchanged.
- **Recipients.** Two sessions at one camera pose with different
  projections show different pictures. One of them is restricted while its
  camera is held still, the replacement picture is deliberately late, and
  the job submitted before the restriction finishes afterwards. Every
  frame delivered after the restriction reflects it, lighting included.
  Two sessions with different bodies and the same static rows share one
  probe volume. A change of grant that moves no visible row relights
  nothing.
- **Standing feeds.** A named still feed that renders today stands once its
  cause is fixed, shown by a count of renders. An external frame, an
  exposure change and newly converging lighting each wake it.
- **Windows and glass.** Two viewers who see different slivers of one
  window each see the right pixels. A window crossing the near plane keeps
  its mapping, and a pick on a known mark at each edge lands on it. A pane
  moved across separate tiles, removed, and its buffer used by another
  view, leaves nothing behind, and the bytes cleared are reported against
  a full clear. Mirrored, skinned and clipped glass face the right way,
  and a rotating glass box reads the same thickness from both sides. With
  the eye under water, two overlapping media and a continuous crossing of
  the waterline show colour that deepens with the length of water a ray
  passes.
- **The history and the probe lookup.** The split history holds through
  camera motion, a newly revealed surface, a view cut and a history just
  allocated. A lit pane over the sky, a lit pane over a dark wall, and two
  panes at an angle are each lit correctly with the lookup shared.
- **Admission.** Under sustained backpressure, under minimize and restore,
  and under swap chain recreation on Vulkan, three counts are kept apart:
  production avoided, submits accepted and display drops observed. A
  skipped attempt, then a replaced swap chain, then a first present on the
  new one, with an answer about the old one still outstanding, are each
  recorded against the right attempt. No frame is rendered without a slot.
  The host keeps serving ticks, input and shutdown. The time from input to
  display is no longer than before.
- **The overlay.** The UI plan's own rendering check passes: proportional
  text, clips, split screen, and the focus treatment that reads the scene
  behind it.
- **Machine cadence.** A scrolling test cartridge is shown at display rates
  below, equal to and above the machine's. Each shows the latest eligible
  frame at a bounded age against the audio clock, with repeats and skips
  counted, and with no upload of an unchanged frame.
- **The Steam Deck.** Its cadence, the lateness of its waits and its
  response to input and shutdown are measured first, and the timing work
  shows an improvement on them. The display line of its report names a
  timing signal, or says there is none.
- The cold bake still reads about a minute or less on each backend.
- The slowest frame of the route holds 16.7 ms on the RTX 2060 and the RTX
  4070, and 22.2 ms on the Steam Deck, read as stated at the top of this
  section. On a device that reports no display time, that is a statement
  about the frames the host produced.

## Invariants the order rests on

Each row is a property that a step above depends on, the one place that
enforces it, and where that guarantee stops. Most are not enforced yet, and
the row says so. The step that builds the mechanism builds the enforcement
with it.

| The property | What enforces it | Where the guarantee stops |
|---|---|---|
| Scene light is converted exactly once before anything reads it as a working image (decision 5) | Not enforced in the current tree. To build: every port of a render graph declares which of three kinds of image it holds, and composition refuses a mismatch. The conversion sits in the producing view's graph | It does not cover the values a kernel writes inside a declared kind. The card and patches through each path and each display output cover those |
| An authored colour is decoded exactly once on its way into shading, and a display colour never is (decision 5) | Not enforced in the current tree: one grammar is parsed at 22 places. To build: a scene colour and a display colour are two types, and only a scene colour can be packed for shading | A site that parses a colour into the wrong type. The list of parse sites and one swatch for each kind of colour cover that |
| Every cascade texel a receiver can read, for as long as that shadow map is shown, is marked, or else the cascade culls nothing (decision 11, option 11b) | Today the far cascade's mark covers its one kind of reader in the frame it is built. To build: the marking uses one geometric bound, the authored maximum radius, and the receiver cannot sample outside it. A mask records its layer, its projection and the region it is valid for, and a receiver outside that region takes the probes' answer. A cascade with no mark keeps every caster | A kind of read added later without widening the bound. The culling comparison in motion covers that |
| A lit, admitted light holds the primary cascade role (decision 6, option 6b) | To build: one resolver assigns roles, and `SdfMeshShadowCascades.Fit` reads roles and not slot 0 | The fade between roles. It is authored, and the walk through the exchange and the crossing check it |
| A view's last picture stands only if nothing it shows has changed (decision 2) | Today the packed hash, taken after the capture, and the rule that a standing view must still be bound to the same resolved view (`SdfWorldResidency.IsUnchanged`, `SdfWorldPasses.Refresh`). Both stay. If the cheap check of decision 2's step 3 is built: one constructor takes every change stamp, and an audit switch compares the cheap answer with the packed hash. Until a check covers it, the cheap answer stays under adversarial review ([hazards](../development/hazards.md#silent-invariants)) | A writer that changes an input without moving its stamp, where the audit is not running |
| Every residency does its upkeep every frame, captured or not (decision 2) | To build: upkeep is its own step, run for every residency before any is chosen for capture | Upkeep that is later added inside the capture. The checks on an unseen residency's memory and shader reload cover that |
| A reused published result was derived only from rows its consumer is delivered (decision 7's shared pool, decision 14's standing feeds) | Not enforced in the current tree. To build: the reuse key is built from the content identities of the delivered inputs a result read, so no reuse can be written without them. The same test runs when an image is bound to a reader and when a finished job installs its result. Until a check covers it, this stays under adversarial review ([hazards](../development/hazards.md#silent-invariants)) | What the authority chooses to deliver, which belongs to [disclosure](disclosure.md). Pixels already delivered |
| A group within reach always has a surface to draw, and a place whose floors do not exist yet is not published with holes (decision 9) | The streaming plan's floor, built by its decision 5. To build: the frame keeps the last complete scene, or a window's authored fallback, until the new place's floors and lighting stand | A floor that fails its acceptance test. Such a group keeps a finer tier |
| A frame whose lighting has converged shows light that is a function of its scene, its lighting and where its probe grid sits (decisions 4 and 7) | A frame the graph withholds drives its own background work to completion and does not pump it under budgets (`SdfWorldResidency.FrameWithheld`). The headroom rule and the per-second grant must stay out of that path. To build: every probe of a batch reads the bounce from before the batch | Ordinary presented play, where budgets apply and the lighting's age varies |
| A query that replaces shared probe state runs alone, and a batch's commit copies only the rows that batch wrote (decision 4, option 4b) | The unmerged branch's single admission door (`SdfMeshProbeGpuWork.TryTake`), by that branch's own account | A running World. The branch has not been measured in one |
| No background job runs longer than one frame's allowance, and no dispatch does more work than its cap (decision 4) | Today only the probe client sizes its jobs. To build: every client sizes a job through one rate type (`SdfGpuWorkRate`) against the live allowance, and a kernel counts its own work against a cap it is given and resumes later | A job whose cost has not been measured yet. The service's count of jobs over the allowance covers that |
| A background job handed to the GPU early keeps its inputs alive until it finishes (decision 4, option 4e) | Not built. To build: one place hands a job to the queue and takes the holds on its inputs, and a job cannot be submitted another way. Adversarial review ([hazards](../development/hazards.md#silent-invariants)) | A lost device |
| A position one GPU job wrote is bounded before another job reads at it (decision 4, option 4c) | Not built. To build: the reading kernel clamps to a length it is given as an input. Adversarial review | Nothing beyond the clamp: a wrong position inside the bounds reads the wrong probe, which the drain comparison covers |
| The rule that finds a surface crossing has one definition (decision 4, option 4d) | Today the rule exists on the CPU only. To build, with the GPU sampling: both evaluators derived from one source | Floating-point differences between the evaluators. The bracket and residual checks measure those |
| Size on screen has one definition (decision 8) | Not enforced: four places compute it. To build: one function in one home, called by all four, with the scale passed to shaders as data | Rounding differences between the host and a shader's use of the scale |
| A rung is drawn by choice only when its recorded deviation, measured over the authored group's own bounds, fits the consumer's budget (decision 8) | To build: the chain selects from records, and a rung with no record cannot be selected | What the measure's sampling can miss. A group with such features is recorded as not qualified |
| What play did to an occurrence survives the eviction of its cell (decision 12, option 12b) | To build: changes are simulation state keyed by the canonical identity and filed under the cell an occurrence is in now. A cell's buffer is a cache | An identity that changes with how the scatter is written. The equivalence check covers that |
| The authority and a presenter evaluate the same occurrences for a cell (decision 12, option 12b) | Today one list serves both, for a whole world. To build: one function evaluates a cell, and both demands call it | Nothing in the arithmetic, which is fixed-point. The comparison of a headless World's cell bytes with a presenting one's covers the rest |
| A present's outcome is credited to the attempt that made it (decision 15, option 15a) | Not built: the driver's id starts again on a new swap chain. To build: the record's key is the host's own attempt number, with the driver's id and its swap chain stored beside it | A driver that reports an outcome with no id |

## Who decides what

Three kinds of choice appear on this page. Most are settled here by
evidence. Look values belong to authors. A short list belongs to the owner.

### Decided here, with no owner call

Each of these is settled by the code, a measurement or a recorded ruling:

1. Measure first, and build the route and the reading of the bar that the
   measuring needs.
2. Capture only the residencies a frame needs, after the root's capture has
   produced the cameras and footprints, with the packed hash kept as the
   judge of freshness.
3. The tick stays ahead of produce and is built in the job system plan's
   order.
4. Background work is budgeted against the one measured reading of the
   frame's load, in jobs that each fit a frame's allowance.
5. One conversion from scene light to the working image, at the edge where
   one becomes the other, with the sharpen ahead of bloom as the register
   rules, and two types for colour.
6. A shadow filter sized in metres, lights admitted by role, and a per-pixel
   probe fallback.
7. What each kind of lighting change costs, for the edits that keep a
   light's reach.
8. One function for size on screen, and one chain of meshes measured
   against the field over the authored shape's own bounds.
9. The streaming plan's order, with this round's additions and its test in
   physical bytes.
10. A mesh's key by bake stage, the contour rule inside the existing
    refinement, and the exact removal of triangles, with the bake's speed
    fixes ranked by one measured run.
11. One instance table in the visibility plan's order, a conservative
    receiver mask for every cascade, and indexed draws as an experiment.
12. One compiled form for motion programs, and placement evaluated cell by
    cell by one function for collision and drawing.
13. One shading path.
14. Consumers' minimums before pixel share, the fitted window, and glass by
    coverage.
15. The present record keyed by the host's own attempt, and the admission
    rule.

### Looks are authored

A look value is an authored parameter with a neutral engine default
([AGENTS.md](../../AGENTS.md#puck-is-pre-release), "Authors rule" and "The
Puck standard is authored"). The standard package (`worlds/standard/`)
chooses the values it wants, and any author can override them. The debug
world's values are set by its artists and judged against the references by
the lead and the look work. The owner corrects a look and is never asked to
choose one.

The table names, for each look value this page touches, the authored
setting that carries it. Where the state says "to build", the engine's work
is the mechanism and its neutral default, never the value.

| Look | Where an author sets it | State today | Engine default |
|---|---|---|---|
| The tone curve and its constants | `render.tonemap` (`WorldTonemap`) | A switch exists. It names one curve whose constants are in engine shader code. To build with decision 5: the curve as authored data in the standard package, chosen per world and per camera | No curve: the encode alone |
| Exposure: the manual value, the bounds, the compensation, the meter's band, percentiles and centre weight, the adaptation time and the local share | `render.exposure` (`WorldRenderExposure`) | Exists. The value, the bounds, the compensation and the hold can each be bound to a literal, a state row, or keys on a clock | `SdfExposure.Default` |
| Exposure per place | The same members, bound to a state row that a world's rules write when the player enters a place. The gallery's inspection lever pins its hold this way | Exists for a stepped change. To build: a blend across a doorway or a portal, as authored data | No per-place behaviour |
| A light's softness | A directional light's `angularRadius` (`WorldRenderLight.Directional`), which can be bound | Exists | The pinned soft edge |
| The widest soft edge, and the blend band between cascades | Members of `render.shadowCoverage` (`WorldRenderShadowCoverage`) | To build with decision 6 (option 6a) | The light's own geometry with no cap, and a band one filter footprint wide |
| How far shadows reach: the near, far and caster distances, and later the cascade radii | `render.shadowCoverage` | Exists as three distances. To build with decision 6 (option 6d): the radii as one authored set | The schema's defaults |
| How far behind a distant shadow may lag | A refresh interval or an angular step on a far cascade entry | To build with decision 6 (option 6d) | No lag: an entry refreshes as its light moves |
| Which lights cast shadows, and how a handover fades | A light's `shadow` mode, and `render.shadowLights`, `shadowFadeSlots`, `shadowFadeTicks` and `shadowOverflow` | Exists | One slot and an instant handover |
| The night | The sky layers, the lights and the exposure bounds keyed on a day clock, as `worlds/debug/night.puck` and `worlds/debug/day.puck` author them | Exists. [The register](../decisions/rendering.md#lights-screens-and-the-camera-are-photometric) rules that how night reads is the author's | No night look |
| Indirect light: the tier, the source gains, the bounces, the lighting grid, the relight and trace allowances and the lighting age | `render.indirect` (`WorldRenderIndirect`) | Exists. With decision 4 the allowances' unit becomes work per second. To build with decision 7 (option 7g): the probe levels (`SdfMeshIrradiance.LevelsOf`) as authored data | The Medium tier and unlimited allowances |
| The quality presets | `render.low`, `render.medium` and `render.high` (`WorldQualityPreset`), authored in `worlds/standard/quality.puck` | Exists | No presets |
| What a nested view gives up at each presented size | A table in the quality presets | To build with decision 14 (option 14c), replacing `WorldSessionSceneEmitter.ReducedQuality` and `WorldScreenBinder.CameraViewQuality` | No restriction |
| The detail budgets: a tier's share of a pixel, a level's error in pixels, and its hysteresis | Members of the quality presets | To build with decision 8 (option 8b), replacing `SdfMeshTier.PixelShare` and `SdfMeshDetail.CoarserPixels` | The values of the recorded rulings. This is the one row whose default is a ruled engine behaviour and not a neutral value |
| How ground cover thins with distance | A curve on a scatter | To build with decision 8 (option 8g) | Everything is kept |
| Bloom, sharpening, the cel style and the atmosphere | `render.bloom`, `render.upscaleSharpness`, `render.toon` with the materials' styles, and `render.atmosphere` | Exist. One default is not neutral: a world that authors no atmosphere still gets fog (`SdfAtmosphere.Default`). That fog is a look held in the engine, and it moves to the standard package with decision 5's retune | Off or absent, once the default fog has moved |
| What a fixed-refresh display shows when the loop cannot hold its rate | A presentation setting beside the quality presets, which a player's option overrides | To build with decision 15 (option 15b), over `PresentPacingPolicy` | Frames as they arrive |
| The back of a portal's glass | A property of the portal | To build. Decision 14's fitted window culls by facing where the authored back shows no image | No image |
| How much of its own exposure a window's world applies before the viewer applies the rest | A member beside the exposure settings | To build with decision 5, replacing the constant `SdfPhotometry.WindowAdaptation`. The standard package authors today's three quarters | None of its own: the viewer's exposure alone |
| The share of the display at which a nested view's update rate steps down | Members of the quality presets | To build with decision 14, replacing the thresholds behind `SdfUpdateRateTiers.ViewHertz` | Every view at the display's rate |

No step of the build order waits for a look to be chosen. A mechanism lands
with its neutral default and with the standard package's value beside it.
After decision 5, every place is judged again against its reference. The
exposure compensation that each place authors today stands in for the
missing encode.

Open items listed several of these values among the owner's sittings and
calls. That page now matches this section: its owner's list holds engine
behaviour and owner-only actions, and the look values point here.

### The owner's calls

The owner's list holds engine behaviour and owner-only actions. Only the
hardware blocks a step. Every other call has a default the work proceeds
on.

1. **Whether a frame of latency ever comes back** (decision 3, option 3c).
   It returns only on the measurement decision 3 names, and it would reverse
   a recorded ruling.
2. **Restricting when authors' shading programs may be interpreted**
   (decision 13). It comes to the owner only when a device has an
   otherwise workable way of running programs and the readings show the
   interpreter alone breaks its frame, with the use that would be lost
   named. A device on which every way misses is not this call: its cause
   lies elsewhere and is fixed first.
3. **The engine default of a recorded ruling.** The one-pixel level, its
   hysteresis and a cascade's texel rule are the owner's rulings, and they
   stay the engine's defaults. An author's quality table may differ. The
   default moves only on the owner's word, with captures from every side
   (decision 8).
4. **The transmission plan's recorded calls and the Steam Deck's budgets**,
   each presented with the design's own choice first
   ([the plan's calls](transmission.md#the-owners-calls)). The plan's Steam
   Deck session consumes them (decision 6, option 6e).
5. **What a bar device does when the planned gains cannot bring it to its
   bar.** Stage 0 measures how far each device is from its bar. If the
   gains planned for a device do not add up to that distance, the order is
   cut again for the device, and what still remains comes here.
   - *Until the owner says otherwise:* the device keeps the highest
     quality, as the register rules. It misses its bar, and every slow
     frame is reported with its cause. The lighting's minimum is still
     served, in jobs bounded to one frame's allowance.
   - *What the owner would choose between:* more engine work aimed at that
     device before anything else; a quality preset below the highest on
     that device, which reverses "lower qualities wait"; dynamic
     resolution on that device, which exists and is off in every preset;
     or a different bar for that device.
6. **The window size the frame bar is read at on the two desktop
   devices.** The register sets the bar at the highest quality and a native
   render scale, and names no size. The benchmark's default window is 1280
   by 720, and one other bar in the register is quoted at 1280 by 900.
   - *Until the owner says otherwise:* 1280 by 720, with a second reading
     at 1920 by 1080 so the cost of a larger window is on record. The
     Steam Deck is read at its own panel's size.
7. **Whether sharpening may act on bloom's scattered light** (decision 5).
   - *What this page does:* it keeps the register's order. The sharpen
     moves into the view's own conversion, ahead of bloom. Nothing in the
     register changes. The cost is that a view must know whether it is
     shown at its own size.
   - *The alternative:* leave the sharpen in the placement pass, after the
     conversion. That is less work. Sharpening then acts on scattered
     light and on encoded values, the register's bloom passage is amended,
     and the sharpen's constants are set again.
   - The debug world authors no sharpening, so the two look the same
     there today.
8. **Hardware.** An RTX 2060 and a Steam Deck for stage 0's device
   measurements. The Deck is needed beside stage 0 and not only at stage
   4, because its windowed path has never run on hardware. An HDR display
   for stage 1's colour check.
9. **The corrected build order.** The owner agreed to the summary's
   priorities before any review, and compares the order above with the
   draft's ([what changed in the build order](#what-changed-in-the-build-order)).

Two calls recorded elsewhere block nothing here. The streaming plan's
questions, how memory pressure feels on a camera turn and what a character
shows at its floor, "have defaults and block no step"
([open items](open-items.md#owner-decisions)). Whether feet appear inside
session scenes changes how much field work a session scene owes, and no step
waits for it.

## What would change the answer

Each item is a result that would change a decision, and what would happen
then.

- **The produce breakdown names a part that no decision covers.** Decision
  2's order follows the breakdown, not this page.
- **Capturing a shown but unchanged residency turns out to be cheap.** The
  cheap freshness check of decision 2's step 3 is not built.
- **The tick's breakdown shows the two partitions save little.** The tick's
  serial fixes come first, and stepping world instances in parallel is
  weighed.
- **Tick and produce each fit a frame, and their sum does not**, after
  decision 2. A tick on its own thread returns as a question, with a
  measured hand-off, as the owner's call.
- **The measured headroom is below the lighting's minimum.** If the GPU
  frame alone misses the bar once the CPU stops being the limit, the
  lighting gets its minimum and no more, in jobs bounded to one frame's
  allowance. The views pass (decision 13, option 13d) and the cascades
  (decisions 6 and 11) then come first.
- **The planned gains do not add up to a bar device's distance from its
  bar.** The order is cut again for that device. What remains is the
  owner's call, with the default stated there.
- **The cold bake is limited by its processors and not by its GPU
  requests**, or the reverse. The fix on the critical path lands first
  (decision 10). The other waits for a measurement that shows it pays.
- **The cold bake is still well over its figure after the first speed
  fix.** The second fix moves up from stage 3. Nothing after stage 2 waits
  for the figure.
- **A bake stage's product is too large to keep.** That stage is not keyed
  on its own, and an edit to it still costs the stages before it.
- **A cube per source group costs more memory than a zone has.** Decision
  7's per-source storage narrows to the directional lights and the sky.
  Local lights keep the full relight.
- **A bar device behaves differently.** A Steam Deck limited by vertex work
  moves three things ahead of the instance table: the removal of redundant
  triangles (option 10b), the indexed-draw experiment (option 11c) and the
  far cascade's floor tier. A device limited by memory bandwidth moves the
  per-pass savings (option 11g) and the planar visibility record (option
  13c) up.
- **Fewer than four probe hits share each surface entry.** The visibility
  bits (option 7c) then need a different store, with compressed rows, and
  hardware ray queries are measured sooner.
- **The floor fails the lighting check for most groups near a fine probe
  level.** The lighting's tier becomes a third mesh there. Its bytes are
  priced against the budget, and the streaming plan's option C is the
  fallback.
- **The floor fails its acceptance test on thin geometry.** The far cascade
  keeps the coarsest collapse level for those groups.
- **Recording a rung's deviation from the field costs too much bake time.**
  The chain stops at the levels of the tier that was asked for. The floor
  is still drawn where a mesh is missing, as detail that is owed. Nothing
  steps down to it by distance, because its coverage test does not bound
  how far it moved an edge.
- **Per-copy tiers pop on thin parts in motion.** The chain stops the same
  way. Meshes baked over many copies and the thinning curve carry the far
  distance.
- **Indexed draws are unsupported or slower on a bar device.** They do not
  become the baseline's format, and the packed vertex layout does not
  return. Grouping goes ahead on today's draws. Where indexed draws pay on
  stronger hardware they may be an enhancement there.
- **The metre-sized filter cannot match a brute-force reference**, to
  within what the views filter leaves over. The blend band stays. The
  pyramid is replaced by more samples, in the near cascade only.
- **The fixed layers and every way of running shading programs miss on a
  device.** Shading programs are not the cause there. The per-phase
  readings name what is, and that is fixed first.
- **The trace shows that machine frames are not dropped** at the debug
  world's tick rate. The machine cadence work (option 15d) shrinks to not
  uploading unchanged frames.
- **An author wants today's appearance.** It is written as a tone transform
  over the corrected conversion, in the world or in the standard package,
  and judged on SDR and HDR. The working image's convention changes only if
  every producer and consumer changes together.
- **A bar device's driver provides no display time.** That device's
  displayed interval stays an absent column. Its frame bar is then read
  from the work of each frame the host produced, with skipped and dropped
  presents reported. That shows what the host did. It does not show the
  bar was met on screen.

## Research still owed

Each item names the first stage that needs it.

- **Display timing on the bar devices**, before stage 0's present record
  is designed. Which present-timing and present-id extensions each
  device's driver exposes, and which stages of a present each surface can
  time, read from the device.
- **Linux and the Steam Deck**, for the bring-up beside stage 0 and for
  stage 4's timing work. Presentation-time and tearing protocols, the
  compositor's frame limiter, timer wake-up jitter, and the driver's
  present wait.
- **Root search and compaction on the GPU**, before stage 2. Nothing we
  read measures either, so stage 2 measures its own.
- **A mesh store shared across GPUs**, in stage 3's store check. Whether
  the bake kernel's floating-point results reproduce across vendors is read
  on each bar device there.
- **The temporal history**, before stage 4's history split. Karis's notes
  and the body of the survey, before the window's layout or its sample
  count changes.
- **Terrain**, before stage 4's tiles. Almost no terrain source was read.
  The open questions are which seam is right between dual-contoured tiles
  at different tiers, weighed against the overlap rule; how tiles scale;
  and what a hybrid with a height field costs. Clipmap and chunked terrain
  are read for the same round.
- **Many lights, and the shadow of an area light**, before stage 4's
  emitters and screens. How a light-emitting face is blocked, sizing a
  light's range from its emitted luminance, and clustered light selection.
  These serve decision 7 (options 7h and 7c) and decision 15 (option 15e).
- **Light and shadow across the edge of a world**, before stage 4's seam
  check. Whether a neighbour's geometry casts shadows and bounces light
  across an adjacency. One limit is known: a seam's shared band holds at
  most eight placements and eight bodies (`WorldAdjacencyGeometry`).
- **Nested views in shipped engines**, and transparency with bounded
  memory, if decision 14 still leaves a portal-crossing frame costing close
  to two views.

## Existing systems each decision extends

Each decision extends a mechanism Puck already has. None adds a second.

| What the work needs | The existing mechanism it extends |
|---|---|
| Schedule a residency's capture | The runtime already hands a frame's footprints to its packages before they prepare (`RenderGraphRuntime`), and the scheduler of views (`RenderGraphScheduler`) takes capture as one more item with an update rate |
| Know how loaded the GPU frame is | Dynamic resolution's reading of the views' GPU time against the display's period (`IWorldFrameLoadSource`, `WorldDynamicResolution`), and the scheduler's unset ceiling on passes times pixels (`RenderGraphFrame.PassPixelBudget`) |
| Run every kind of background GPU job | The one background service and its budget (`SdfGpuWork`, `SdfGpuWorkBudget`), and its measured rate per kind of work (`SdfGpuWorkRate`). Its probe client (`SdfMeshProbeGpuWork`) already chains a cache kernel, a compaction and an indirect dispatch |
| Say what an image holds | How an image source's values are encoded (`ImageColorEncoding`) and the conversion of a source into the working image (`ImageSourceConversion`) |
| Give cascades to lights by role | The allocator's ranking of candidates (`WorldShadowAllocator`) |
| Compaction, grouping, every cascade's mask and the glass list | The one place that classifies draws (`SdfMeshOcclusion`) |
| Requests for detail, and tiers | `SdfMeshDemand` and `SdfMeshTier`, under the selector (`SdfResidencySelector`) and the memory account (`MemoryAccount`). Baked meshes stay in `SdfMeshLibrary` and `SdfMeshStore` |
| Measure a rung against the field | The existing measure of a level against the field's surface (`SdfMeshFieldLevels.Measure`) |
| Sample a cell more finely | The bake's refinement of a flagged cell (`SdfSampledLattice.Refine`, `SdfDualContouring.ExtractRefined`) |
| Work for worlds nobody sees, and a view's share of the display | The update-rate tiers (`SdfUpdateRateTiers`), the host's measure of each view's share (`WorldViewGraphHost`), the test of whether a portal is in sight (`SdfPortalSight`) and the cached field answers (`WorldFieldAnswer`) |
| Fit a window's camera to its glass | The existing fit through the whole opening (`WorldWindowFrustumFit`) |
| Shading, and the kernel for a motion program | `SdfShadeProgram` and its table of operations (`SdfShadeOps`), and the bins of `SdfShadeDispatch`, which glass reuses |
| The occurrences of a placement, and what play did to them | The compiled list both drawing and collision read (`CompiledPlacementStamp`), and keyed state over placements (`WorldPlacementDeal`, `WorldPlacementResponse`) |
| Sharing static geometry, and moved transforms | `WorldRigidAssets` and `SdfMovedTransforms` |
| When a present was shown, and waiting for room to present | What every presenter reports (`IPresentTimingFeedback`, `PresentTimingSample`) and the wait both backends share (`ISwapChainPacing`) |
| Waits, workers, GPU completion, pacing and the frame's timeline | `HostWaits`, `JobPool`, `GpuQueueTimeline`, `PresentPacingPolicy` and `HostFrameTimeline` |

### Outside techniques not taken

The round considered these techniques from outside Puck and did not take
them. The last column says what would reopen each.

| Technique | Verdict | Why | What would change it |
|---|---|---|---|
| Virtual shadow maps with cached pages | Rejected. The virtual geometry plan's shadow decision (D8) stands | A moving light and deforming casters invalidate cached pages, and the day clock and the wind run by default. The one account of a moving sun could not be fetched | A measurement showing that redrawing static geometry under unchanged projections dominates a cascade |
| Sparse shadow pages without caching | Waits for a measurement | The culling half is the receiver mask Puck already has. The storage half is unmeasured | The occupied share of a cascade's cells measured to be small |
| Refitting the projection every frame, scrolling cascades, static slices for the sun, baked time-of-day visibility | Rejected | Each gives up the snapped, held fit, or assumes a still light or a fixed world | Nothing, while the day clock is authored |
| Moment, variance and exponential shadow maps | Rejected for opaque shadows. Open for glass | Light bleeds between storeys. For translucent casters, the moment paper's experiments blend sorted geometry and name unsorted alternatives such as stochastic transparency (unverified). So sorting is one implementation and not a requirement. Puck's sum of optical depth is exact in any draw order, but only for the whole span down to the first opaque surface. It cannot say how much glass lies in front of a receiver that stands between two panes, which is what storage by depth answers | The check decision 6 names: a receiver in front of, between and behind two panes |
| Sun shadows by ray tracing or by marching the field | Rejected for the baseline | It puts a field march in the frame, or it is a second implementation with no stable baseline to match | Decision 6 landed, and a measurement on the RTX 4070 |
| A cluster hierarchy traversed on the GPU. A simplifier library as a dependency | Watched | A second builder beside the one mesher. Weak on aggregates of small things. Puck is independent work | The virtual geometry plan's cluster measurement (G9) |
| Mesh shaders | Enhancement only, and not built | The visibility plan records the feature as present on both RTX cards' generations and enabled by the Steam Deck's open driver, with the actual drivers unqualified. So the reason is not that the hardware lacks it. Puck's backends have no mesh-shader path, and it would be a second way to draw | The feature read from each bar device, the baseline holding, and the path measured to pay with a fallback |
| Work graphs, device-generated commands | Enhancement only | Work graphs are absent on the RTX 2060 and the Steam Deck and have no Vulkan equivalent. Device-generated commands were not read on any bar device | The baseline holds, and the path is measured to pay, with a fallback |
| A rasterizer written in compute | Deferred | Hardware rasterization is the qualified baseline. The cited benchmark's depth-only variant runs on mobile parts with 32-bit atomics (unverified), so a portable subset exists. A visibility id with depth, clipping and depth ties, and Puck's cost on it, are unqualified. No pass has been shown to be limited by what it would replace | A bar device measured to be limited by rasterizing small triangles, with the atomic width or other feature the chosen variant needs named |
| Arrivals requested by the GPU alone, pages below the mesh, slabs | Rejected | Decision 9: a group that is not in memory cannot report | Nothing |
| Hints for refinement from geometry already in memory | Held as an experiment | Decision 9 (option 9d): prediction keeps arrivals and camera turns, and the hint uses the observation channel | Over-fetching or slow refinement still measured once observation decides what is kept |
| Transition cells between tiers, geomorphing | Rejected | Both tie a tile to its neighbour's tier or add a position per vertex | Pops shown in motion after decision 8 |
| Contouring or placement on the GPU. Contouring without gradients | Rejected, and watched | A second mesher. Only an abstract was read | The paper read in full |
| A cap on triangles | Rejected | Detail follows size on screen, not a count | Nothing |
| An error measure for the collapse that includes attributes (quadrics with attributes) | Open to a measurement, inside the one collapse | An earlier draft rejected it for good on the grounds that it cannot model creases. That was wrong: the method penalizes moving a vertex off a sharp edge, though it does not promise every seam survives. As a second simplifier it stays rejected | It is tried inside the existing collapse, under its crease and material rules, and judged by deviation from the field, thin features and bake time |
| Radiance caches fed from the screen or by reservoirs, and per-pixel gathers, as whole systems | Rejected, with ideas taken | They are per view, stochastic or dependent on history, which breaks a reproducible drain | A sparse cache whose behaviour from cold is measured to be no worse |
| Hysteresis, random rotation, moment-based visibility, probes switched off, frozen lighting, a drain started warm | Rejected | The relight is a fixed quadrature, and stale light is the defect being fixed | Nothing |
| Hardware ray queries for probe rays | Deferred | Decision 7 removes most of the rays it would speed up | Shadow rays still dominating after shared surface lighting (option 7b) |
| Ray-traced supersampling of edges, cone-traced supersampling, resolve weights that follow exposure | Rejected, or not judged | The first needs hardware ray tracing. The second is a paper nobody read. The third has no source | A shimmer on a bright edge in a moving capture |
| A visibility record of 4 to 8 bytes | Rejected | It moves work into a kernel that is already limited by registers | Nothing |
| A bloom threshold, exposure kept on the GPU, local tonemapping in image space, glare, a default grading table, fused post passes | Rejected or deferred | The bloom decision is settled. Exposure has consumers on the CPU. Local tonemapping makes halos. A grade or a glare is a look, which an author adds as data and the engine does not default | A measured need for the mechanism, or an author's program for the look |
| Weighted, moment or voxel transparency, per-pixel lists, a two-layer deep buffer | Rejected | They approximate sums Puck computes exactly, or they need unbounded storage | Many overlapping translucent layers |
| Stencil portals, layered multi-view, planar reflections, a screen-space march on glass as the baseline | Rejected or gated | A second mechanism for nesting views. The last waits on the water plan's gate | A portal-crossing frame costing close to two views |
| A tick on its own thread, or a pipelined tick | Rejected as a fix for frame time | Decision 3 | The measurement decision 3 names |
| Parallel recording now, a submission thread, fibers, switching the garbage collector's mode | Held or rejected | In order: its own admission rule, contracts that assume one thread, the rule about the host, and a stopgap beside a fix at the cause | Recording measured at several milliseconds |
| Generation every frame, generated blades, wind as a vertex offset, impostors, a learned field, height-field terrain as the engine's own | Rejected | Decision 12. Each is a second representation | The terrain round's evidence |
| Outline text as the baseline, compute binning for 2D, an oblique near plane, texel shading, a vendor's latency kit, exclusive fullscreen | Rejected or later | Costlier at small sizes. No evidence was read. A second mechanism. Tied to one vendor. It takes the display away from the host | Large text in the world, or a measured need |

## The review

A reviewer from another model family read this synthesis against the code.
It produced findings only and took no GPU measurement. Its host stopped it
for low memory after it had written thirty-five findings and four passages
it judged sound. It has since been run again on the corrected page, and
[the next section](#what-the-attack-and-the-second-review-changed) answers
that second review together with a separate attack on the page.

Each finding was checked in the current source before it changed anything.
All thirty-five hold. "Fixed" means the page changed where the last column
says. One finding is fixed with part of the original step kept, and the row
gives the reason. Where a later review changed an answer again, the last
column gives the answer as it stands now.

| Finding | Severity | What it found | Outcome | Where it is answered |
|---|---|---|---|---|
| RR-01 | High | The proposed colour conversion could be skipped or applied twice: a single view, a pane and a filmed view each bypass or double it | Fixed | Decision 5: each image declares which of three kinds it holds at the graph's edges, and the conversion is in the producing view's graph, checked through every path and every display output |
| RR-02 | High | A floor can pass its silhouette check and still fail for lighting | Fixed | Decisions 7 (option 7d) and 8: probe spacing selects the lighting's tier, and the floor is shared only where a lighting check passes |
| RR-03 | High | The caster culling leaves out receivers that this plan itself adds | Fixed, first step kept | Decision 11 (option 11b): the mask is a union by construction, built from one geometric bound and valid for as long as its shadow map is shown. The mask for the near and second cascades stays an early step. In the current tree an opaque surface reads the first cascade that contains it and glass reads none, so no reader exists yet that the opaque mark misses |
| RR-04 | Medium | The 26 cm limit is a sideways limit | Fixed | Decision 6: the sideways reach, the gap along the light and the filter radius are stated separately, with a check for each failure |
| RR-05 | High | The thread pool landed after the work that needs it | Fixed | Decision 3 and stage 2: bounded waits, then the pool, then the partitions, with threads counted by kind |
| RR-06 | High | The budget that higher GPU use needs landed two stages late | Fixed | Decision 4: a headroom rule on the one budget and the one reading of the frame's load, in stage 1, before any step raises use |
| RR-07 | High | The proposed lighting storage changes when a lamp's brightness changes | Fixed | Decision 7 (option 7a): source groups with their own bounce, the multiply-only class of edits, a fixed reach for a local light's brightness as a change to the register, memory priced first, and seven comparisons with a fresh drain |
| RR-08 | High | Scheduling first depends on a freshness question that already captures | Fixed | Decision 2: only the residencies a frame needs are captured, after the root's capture has produced the footprints. The packed hash stays the judge of freshness, and state is keyed by view |
| RR-09 | Medium | A stable view already republishes its last output | Fixed | Decision 14 (option 14d): the existing rule is extended after one feed's cause is explained |
| RR-10 | High | Refusing a dark light does not necessarily promote the lit one | Fixed | Decision 6 (option 6b): roles assigned by name, and the exchange walked in both directions |
| RR-11 | High | The rewrite of the shadow receiver came before the identity data it reads | Fixed | Decision 6 (option 6a) and stage 2: the prototype behind a switch, and the production rewrite after `transmit-identity` |
| RR-12 | High | The unmerged lighting branch still grants work per frame | Fixed | Decision 4 (option 4b): the batch commit that is built and the per-second grant that is not are separate steps |
| RR-13 | Medium | The sky's cache key leaves out the acquired image | Fixed | Decision 7 (option 7f), and the three renderers restated with their conditions |
| RR-14 | Medium | Sessions do not map one to one onto residencies | Fixed | Decisions 1 and 11 (option 11f) |
| RR-15 | Medium | Sharing a store can turn the second backend's cold run into a warm hit | Fixed | Decision 10 (option 10f): each backend's bar is timed from its own empty store. The claim that the bar is met was wrong and is removed |
| RR-16 | High | The ordering of the cold bake's fixes rested on a different display state | Fixed | Item 2 of the disagreements, and decisions 1 and 10: one run under the recipe ranks the fixes |
| RR-17 | Medium | A predicted ray charge was reported as executed work | Fixed | Decision 7, and decision 1's measurement |
| RR-18 | High | Stage 0 required a display observation that a host may never provide | Fixed | Decisions 1 and 15 (option 15a) and stage 0: a capability, or an absent column with its follow-up, and a record keyed by the host's own attempt |
| RR-19 | Medium | Admission, queue depth and physical display carry different guarantees | Fixed | Decision 15 (option 15b), and the duplicates table, which no longer treats the counts of frames in flight as one number |
| RR-20 | Medium | The machine cadence check required frame rates that cannot exist | Fixed | Decision 15 (option 15d) and stage 4 |
| RR-21 | High | A tier qualified by coverage has no error bound across tiers | Fixed | Decision 8 (option 8c): every rung is measured against the field over the authored shape's own bounds, and a switch between two rungs budgets both deviations |
| RR-22 | High | The eviction check could pass without releasing physical memory | Fixed | Decision 9 and stage 3 |
| RR-23 | Medium | Sampling for the bake on the GPU would create a second authoritative root-search rule | Fixed | Decision 4 (option 4d): one source for both evaluators |
| RR-24 | Medium | Two owner questions that block nothing had become prerequisites of a stage | Fixed | The owner's calls, and each stage's needs |
| RR-25 | Medium | The tile-size result came from a different kernel | Fixed | Decision 13 (option 13a): the inference is removed, and the three phases are measured separately |
| RR-26 | Medium | The moment shadow paper permits unsorted implementations | Fixed | The table of outside techniques |
| RR-27 | Medium | Compute rasterization was classed by the wrong capability | Fixed | The table of outside techniques, as its own row |
| RR-28 | High | Share of presented pixels cannot express every consumer of a world | Fixed | Decisions 2 and 14 (option 14a): consumers' minimums first, landing in stage 2 with the capture order |
| RR-29 | Medium | A contour check that passes can hide lost surfaces | Fixed | Decision 10 and stage 1: judged over the whole world, with the audit's own blind spot stated |
| RR-30 | Medium | Stage 1 still hard-coded a visible policy for nested views | Fixed | Decision 14 (option 14c's first step), in stage 1 |
| RR-31 | Medium | Explaining cost alone lets a slower stage pass | Fixed | The build order: a claimed gain and what would show it false for each check, and each landing compared with its frozen base against the base's own spread |
| RR-32 | Medium | Predicted arrivals and refinement requests from the GPU can coexist | Fixed | Decision 9 (option 9d) |
| RR-33 | High | Sharing a published result must respect the recipient's view | Fixed | Decision 14 and the invariants: a result is keyed by the content of the delivered rows it read, and tested again when it is bound and when it is installed |
| RR-34 | Medium | A harvested occurrence needs authoritative state outside the cell cache | Fixed | Decision 12 (option 12b): state keyed by occurrence and filed under the cell the occurrence is in now |
| RR-35 | Medium | The colour conversion was treated as a look for the owner to choose | Fixed | Decision 5, and what would change the answer |

The four passages the review judged sound are unchanged:

- decision 3's tick order and its ruling on latency;
- the out-of-date premise about the occlusion test (item 8 of the
  disagreements);
- decision 14's diagnosis of nested views;
- the two outside figures kept in their original scope: Apers's ray budget
  and Cyberpunk's refresh of distant shadows.

### What changed in the build order

The sequence of stages is the summary's: measure, fix what the player
sees, the frame thread and the round trips, the table and the chain, then
scale. The first review moved work between them, as listed here. What the
two later reviews moved is in
[the next section](#what-moved).

- **Stage 0 gained six measurements.** The display outcome, as a capability
  or an absent column for each device and backend pair (RR-18). Captures
  counted by cause (RR-08). The split of the GPU frame (RR-06). One cold
  bake that gives its critical path (RR-16). Counted shadow queries (RR-17).
  Sessions named by residency (RR-14).
- **Stage 1 gained the headroom rule**, which the draft had in stage 4
  (RR-06). It comes ahead of the probe jobs, which are now two steps
  (RR-12). The colour conversion moved from the `place` pass to the view's
  own graph and is checked through five paths (RR-01, RR-35). Admission to
  slots became cascade roles, in one piece of work with the per-pixel
  fallback (RR-10). "The two quality constants made one" became the authored
  table, moved up from stage 4 (RR-30). The contour check gained a coverage
  test (RR-29). The look pass after decision 5 is the lead's (RR-24).
- **Stage 2 gained the thread pool**, from stage 3, between the bounded
  waits and the partitions (RR-05). It gained `transmit-identity`, from
  stage 4, ahead of the shadow filter (RR-11). Capture gained its snapshot
  (RR-08). Per-source lighting narrowed to edits that keep a light's reach
  (RR-07). The sky's key gained its pictures (RR-13). The caster masks
  widened (RR-03). The bake's GPU sampling stopped being the stage's fixed
  speed fix: the critical path now picks between it, built from one
  root-search rule, and the Land stage (RR-16, RR-23).
- **Stage 3 lost the pool** and takes whichever bake fix stage 2 did not.
  Its chain is measured against the field (RR-21). Its eviction is judged in
  physical bytes (RR-22). The lighting's tier is the lighting's own class of
  demand (RR-02). One store is judged separately from the bar (RR-15).
  Shading's phases are measured separately (RR-25). No chapter may be slower
  at the stage's end (RR-31).
- **Stage 4 lost three things:** the headroom rule, the first step of the
  quality table, and `transmit-identity`. It gained consumers' minimums
  (RR-28), reuse keyed by recipient (RR-33), occurrence identity with
  authoritative changes (RR-34), the explanation of why a still feed renders
  (RR-09), and restated checks for admission and machine cadence (RR-19,
  RR-20). "The owner's reviews" left its list of needs (RR-24).

### What the review did not reach

The reviewer left no account of what it had read. This list is inferred
from where its findings and its four sound passages point. Nothing below had
a finding or a "sound" against it after the first review. That meant it was
not reviewed, not that the reviewer agreed with it. The attack and the
second review have since read most of this list, and their findings on it
are answered in the next section.

- Decision 1's measurements, apart from the display row and the list of
  devices.
- Decision 4's work list on the GPU, early hand-off and second queue
  (options 4c, 4e and 4f).
- Decision 6's eye-centred cascades, per-body layers and glass shadow
  storage (options 6d, 6e and 6f).
- Decision 7's shared surface lighting, coarse-to-fine convergence and
  emitting surfaces (options 7b, 7e and 7h). Its visibility bits and probe
  pool (options 7c and 7g) beyond the one point each drew.
- Decision 8's single projection function, meshes over many copies,
  clusters and thinning (options 8b, 8e, 8f and 8g).
- Decision 10's contour rule, triangle removal and Land stage as designs
  (options 10a, 10b and 10d). Only their checks and their order drew
  findings.
- Decision 11's compaction, indexed draws, skinned boxes, record split,
  per-pass savings and clusters (options 11a, 11c, 11d, 11e, 11g and 11h).
- Decision 12's motion programs and terrain tiles (options 12a and 12c).
- Decision 13's history split, planar record, decoupled lookup and shared
  meter (options 13b to 13e).
- Decision 14's fitted window and its three glass options (14b, 14e, 14f
  and 14g).
- Decision 15's overlay, screens as lights and Linux timing (options 15c,
  15e and 15f).
- [Work the areas do more than once](#work-the-areas-do-more-than-once),
  [research still owed](#research-still-owed), most rows of the table of
  outside techniques, and the source lists.
- Everything the corrections added: the snapshot, the declaration of what an
  image holds, cascade roles, the headroom rule, the source-group class of
  edits, the chain measured against the field, the authored looks and the
  invariants.

## What the attack and the second review changed

Two more reviews read the page after the first.

- **The attack.** Ten readers each attacked the page from one angle:
  whether its code citations are true, whether its sources say what it
  claims, the build order, the rules in AGENTS.md, MMO scale, measurement,
  missing work, the bar devices' hardware, existing systems, and risk.
  Their overlapping findings were merged into 93. A second set of readers
  then tried to refute each one, two readers for each finding raised as
  critical or high and one for each of the rest. They confirmed 2 as
  raised and 90 in a narrower form, and disagreed about 1. None was
  refuted outright. They lowered the severity of 78. Every reader in the
  attack was of the same model family as the page's authors.
- **The second review.** The reviewer from another model family, whose
  first review was cut short, read the corrected page in full. It raised 42
  findings, 17 of them high. It also went back over the first review's 35
  answers. It found that 19 hold and that 16 hold only in part, each with a
  case the correction did not cover. Those cases are among its 42 findings.

Both reviews read code, documents and sources. Neither ran anything. Both
quote the page as it stood before it was rewritten in plain prose, so each
finding was matched to the present text before it was answered.

All 135 findings are answered by a change to this page. None was left as it
was. In a few, part of the proposed change was not taken, and the tables
below say which part and why. The attack's findings had already been
checked against the code by its own second readers. For the second
review's, the code each one cites was opened for this page. Three of them
rest on an outside source that was not reopened here: the paper on
transmittance to each surface behind decision 6's glass limit, the paper on
quadrics behind the reopened collapse measure, and the dual contouring
paper's remark on its threshold. The page records those as the reviewer's
reading.

### What moved

- **Stage 2's capture order was redesigned.** The first correction had a
  cheap snapshot run before the schedule. The cameras and footprints that
  snapshot needed are produced by the root world's capture, so the order
  was not possible. The page now captures only the residencies a frame
  needs, after the root's capture, and keeps the packed hash as the judge
  of freshness. Letting a picture stand on change stamps became an
  optional later step with an audit (decision 2).
- **The minimums for unseen worlds moved from stage 4 to stage 2**, because
  stage 2 removes the hidden render they replace.
- **Compacting the draw list got a stage.** It and the two instance-update
  steps the visibility plan puts ahead of it moved into stage 2. Grouping
  stays in stage 3. Indexed draws changed from part of that format change
  to an experiment.
- **The headroom rule was rebuilt on the load reading dynamic resolution
  already takes**, and cutting bake jobs to a frame's allowance joined it
  in stage 1 (decision 4).
- **The colour fix now lands together with the retuned world**, and the
  sharpen moves into the view's conversion so that the register's order of
  sharpen and bloom is kept (decision 5).
- **The bake decision leads with what an edit costs.** A mesh's key by bake
  stage is new and sits in stage 1. The run that ranks the bake's speed
  fixes moved from stage 0 to the close of stage 1 (decision 10).
- **The per-source relight grew three parts:** each group's own bounce, the
  rounding grid moved to the combined result, and the rescale of probes a
  relight has not reached yet (decision 7).
- **Placement by cell became the placement's own**, evaluated by one
  function for collision and for drawing (decision 12).
- **Reuse is keyed by the content a result read**, not by a counter for
  each recipient, and is tested again when a picture is bound and when a
  job installs its result (decision 14).
- **Steps that had no stage got one:** the clear of glass shadow storage by
  tile and keeping what was observed in use (stage 3), and light from
  emitting surfaces, screens as selected lights and thinned ground cover
  (stage 4).
- **How every check is read is now stated once**, at the top of the build
  order: how the bar is read, how a change is judged, and that needs are
  per item.

### What stage 0 gained

- The benchmark applying the quality preset the bar is defined at, and
  recording the conditions two runs must share.
- The route's missing chapters, a way to reach night that a route can run,
  and a capture at a chapter's tick.
- The Steam Deck's bring-up, beside the stage.
- Eleven measurements: the tick by phase and by world instance, deliveries
  by session, the time from input to display, the per-frame GPU span,
  matched readings of the background work's use of its allowance, a cold
  arrival in play, the time from an edit to seeing it, per-level lighting
  work in motion, triangle work per raster pass, a restricted window's
  readings, and a mesh audit over the whole world.
- One table for each bar device of how far it is from its bar, by part.
- A last column in decision 1's table saying where each reading is taken
  and which step first needs it, so the stage can close device by device.

It lost one thing: the cold bake that ranks the speed fixes, which is now
taken at the close of stage 1.

### What is now the owner's call, and why

Four entries in [the owner's calls](#the-owners-calls) are new, and one is
restated.

- **What a bar device does when the planned gains cannot reach its bar.**
  The register forbids a lower tier as the fix for a missed bar, so this
  page cannot choose one. Only the owner can relax the bar, the quality or
  the device. The engine's behaviour until then is stated there.
- **The window size the bar is read at on the desktops.** A frame-rate bar
  means little without one, and no ruling or plan names it. The page works
  at the benchmark's default and reads a second size for comparison.
- **Whether sharpening may act on bloom's scattered light.** Decision 5
  could be built either way. One way keeps a recorded decision and the
  other amends it. The page keeps it, and the owner may reverse that.
- **Hardware.** The Steam Deck is needed beside stage 0, not at stage 4,
  and an HDR display is needed for stage 1's colour check.
- **Restated: restricting when authors' shading programs are interpreted.**
  It was written as following from a matrix rule that cannot choose. It
  now comes to the owner only when a device has a workable path that the
  interpreter alone breaks.

### What only one model family has read

The second review, from another family, did not examine the redesign of
the capture order, because the attack's findings on it came later. So
decision 2's order, its optional stamp-based check, its upkeep step and its
handling of a withdrawn view have been read by the authors' family only. A
reader from another family should take those four before stage 2 is
briefed.

### Every finding from the attack

Severity is as the attack's second readers corrected it, with the severity
as raised in parentheses where it differs. Where two readers disagreed, the
higher is shown.

| Finding | Severity | What it found | Outcome | Where the page changed |
|---|---|---|---|---|
| RA-01 | High (raised as critical) | Nothing sized how far the weaker bar devices are from their bars | Fixed | Decision 1 (the device rows and the paragraph after the table), stage 0's done list, the owner's call 5, what would change the answer |
| RA-02 | Medium (raised as high) | The route the checks are read on did not exist | Fixed | Summary, stage 0 (what lands and its done list), the words table |
| RA-03 | Medium (raised as high) | How the frame bar is read was never pinned down | Fixed | The build order's rule on how the bar is read, stage 0, the owner's call 6 |
| RA-04 | Medium (raised as high) | "No slower" had no baseline, no tolerance and no rule for a spoiled chapter | Fixed | The build order's rule on how a change is judged, the last done item of stages 2 and 3 |
| RA-05 | Medium (raised as high) | The Steam Deck's readings assumed a windowed path that has never run | Fixed | Stage 0 (what lands, needs, done list), decision 15's risks, the owner's call 8 |
| RA-06 | High | The headroom rule duplicated a GPU load reading the engine already has | Fixed | Decision 4 step 1, the duplicates table, the existing-systems table, stage 1's lighting check |
| RA-07 | Medium (raised as high) | The order in which work yields under overload could hold a weak device below full quality | Fixed | Decision 4 step 1 (what gives way under overload), stage 3's last done item |
| RA-08 | Medium (raised as high) | The headroom check could pass without testing anything, and the feedback loop was undescribed | Fixed | Decision 1 (the per-frame GPU span row), decision 4 step 1, stage 1's lighting check |
| RA-09 | High | Bake jobs are sized by fixed constants and run several frames long | Fixed | Decision 4 (the problem, step 2, option 4d's cap), decision 9 step 2, stage 1's lighting check, the invariants |
| RA-10 | Medium (raised as high) | The 44-second lighting lag was blamed on round trips alone | Fixed | Summary, the disagreements' item 12, decision 4 (the problem and step 3), stage 1's lighting check |
| RA-11 | Medium (raised as high) | Moving bloom ahead of the sharpen contradicted a settled ruling | Fixed | Decision 5 (the conversion's order, fidelity, the camera read two ways, the option not taken), stage 1's colour check, the owner's call 7 |
| RA-12 | High | The cheap snapshot read things that only the capture produces | Fixed | Decision 2 (the problem, step 2, the risks), stage 2's capture check |
| RA-13 | High | Letting a picture stand on change stamps had no check for a picture that should have redrawn | Fixed | Decision 2 step 3 and its risks, stage 2's freshness check, the invariants |
| RA-14 | High | Skipping the prepare for unseen worlds dropped upkeep the prepare also does | Fixed | Decision 2 (the problem, step 2), stage 2 (what lands and the capture check), the invariants |
| RA-15 | Medium, contested (raised as high) | The rule that a reused picture matches what its viewer may see could be lost in stage 2 | Fixed | Decision 2 step 2 (freshness keeps its one judge), stage 2's capture check, the invariants |
| RA-16 | Medium (raised as high) | A reuse key that always carries the recipient shares nothing | Fixed | Decision 14 (reuse must respect who is watching, and still share), the combinations list, the invariants, stage 4's recipients check |
| RA-17 | Medium (raised as high) | Every check ran with all worlds in one process at full disclosure | Fixed | Decision 1 (the restricted-window row), the build order's rule on stating the arrangement of worlds, stage 3's table check |
| RA-18 | Medium (raised as high) | A world nobody sees still costs its tick and its deliveries | Fixed | Decision 1 (two rows), decision 2's "what it rests on", decision 3; an entry in open items |
| RA-19 | High | The list of a scatter's occurrences already exists, and evaluating it per renderer cell would fork collision from drawing | Fixed | Decision 12 (the problem, step 2, the risks), the invariants, stage 4's populations check |
| RA-20 | Medium (raised as high) | Scale was proven on multiples of one world, and seams between worlds were not looked at | Fixed | Stage 4 (needs, the populations and seams checks), research still owed |
| RA-21 | Medium (raised as high) | The planned tick partitions do not reach what the debug world's tick spends | Fixed | Decision 1 (the tick row), decision 3 (step 4 and the paragraphs after it), stage 2's pool check |
| RA-22 | Medium (raised as high) | Decisions rested on input latency, and nothing measured it | Fixed | Decision 1 (the latency row), decision 3's unknowns, decision 15 steps 1 and 2, stage 4's admission check |
| RA-23 | Medium (raised as high) | The fix for blotchy light from emitting surfaces was in no stage, though a sister plan already holds the technique | Fixed | Decision 7 step 10 and its risks, stage 4, research still owed; a cross-reference in the illumination plan |
| RA-24 | High | Compacting the draw list had no stage, though a stage 2 check needed it | Fixed | Decision 11 steps 1, 4 and 5, stage 2 (what lands and the draw-list check), stage 3 |
| RA-25 | Medium (raised as high) | The colour fix was accepted before the world was retuned to it | Fixed | Decision 5 (the mechanism lands with the retuned world), stage 1's retune check |
| RA-26 | Medium (raised as high) | Switching nested-view shadows by size rebuilds a render graph at each threshold | Fixed | Decision 14 step 3 and its risks, stage 1's nested-quality check |
| RA-27 | Medium (raised as high) | The contour fix was checked on fourteen exhibits with an audit that does not cover the world | Fixed | Decision 1 (the audit row), decision 10 (how a contour fix is judged), stage 1's contour check |
| RA-28 | Medium (raised as high) | The GPU work list and the early hand-off landed with no check and no invariant | Fixed | Decision 4's risks, stage 2's background-work check, the invariants |
| RA-29 | Medium (raised as high) | The per-source relight claimed no precedent, though a shipped game rescales its probes | Fixed | Summary, decision 7 (step 4, the first "why" bullet, the risks, the evidence) |
| RA-30 | Medium | Authored colour has no single place where it is packed | Fixed | Decision 5 (authored colour is decoded by its type), stage 1's colour check, the invariants |
| RA-31 | Medium | The HDR check had no instrument and expected the wrong value | Fixed | Decision 5 (the lane builds its instruments first), stage 1's colour check |
| RA-32 | Low (raised as medium) | Stage 1's pieces were called independent and are not | Fixed | Stage 1 (what lands, needs, the lighting check), the owner's call 8 |
| RA-33 | Low (raised as medium) | Stage 2 called one pair independent while other pairs share code | Fixed | Decision 4 steps 4 and 5, stage 2's paragraph on what is serial |
| RA-34 | Medium | Stage 0's bake ranking would be stale by the time it was used, and the minute was checked once | Fixed | Decision 1's risks, decision 10 step 5, stage 1's ranking bake, stages 3 and 4, decision 8's risks |
| RA-35 | Low (raised as medium) | Stage 0 could close without five of its own table's readings | Fixed | Decision 1's table (the last column and the cascade row), stage 0's done list |
| RA-36 | Medium | Some proposed steps were in no stage, and some landed items had no check | Fixed | The "what lands" and done lists of stages 1 to 4 |
| RA-37 | Low (raised as medium) | Whole-stage needs let one unmet item hold everything after it | Fixed | The build order's rules, each stage's needs, what would change the answer |
| RA-38 | Low (raised as medium) | The cold-bake figure never takes the slow round trip the plan blames | Fixed | Decision 1 (the cold-arrival row), decision 4 steps 4 and 5, the build order's reading conditions |
| RA-39 | Low (raised as medium) | The per-second grant and the measured upload rate already have homes | Fixed | Decision 4 step 3, the existing-systems table |
| RA-40 | Medium | Lighting lag was judged by a median at one probe level, standing still | Fixed | Decision 1 (the per-level row), stage 1's lighting check |
| RA-41 | Low (raised as medium) | Nested views regain shadows with no cost check, and one shadow-memory option was stale | Fixed | Decision 6 (the problem and step 6), stage 1's nested-quality check, stage 3 |
| RA-42 | Low (raised as medium) | The check at the edge of the shadow maps ignored the limits the plan itself listed | Fixed | Decision 6 step 2 and its risks, stage 1's shadow check |
| RA-43 | Low (raised as medium) | A residency's probe windows follow its first view | Fixed | Decision 6 step 2, stage 1's shadow check |
| RA-44 | Low (raised as medium) | The shadow filter's depth pyramid was said to have no outside source | Fixed | Decision 6 step 3, its "why" and its evidence, the sources |
| RA-45 | Low (raised as medium) | Two steps seemed to size a light's range by opposite rules | Fixed | Decision 7 step 6 |
| RA-46 | Medium | The source groups left out screens and openings | Fixed | Decision 7 step 3 and its risks, stage 2's lighting check |
| RA-47 | Low (raised as medium) | The probe level table stays engine data until stage 4 | Fixed | Decision 7's risks |
| RA-48 | Low (raised as medium) | The lighting check that qualifies a floor was never defined | Fixed | Decision 7 step 7, stage 3's lighting-tier check |
| RA-49 | Low (raised as medium) | A third word in each cached hit arrives after stores are built on two | Fixed | Decision 7 step 5 and its risks |
| RA-50 | Medium | "One projection function with a host twin" is two implementations | Fixed | Decision 8 steps 1 and 2, the duplicates table, the invariants, stage 3 |
| RA-51 | Low (raised as medium) | A rung's qualification did not name the error measure that already exists | Fixed | Decision 8 step 4, the existing-systems table |
| RA-52 | Low (raised as medium) | The detail budgets are both an authored look and a ruled engine default | Fixed | Decision 8's "what it rests on", the looks table |
| RA-53 | Low (raised as medium) | Memory pressure coarsens everything by one shared amount | Fixed | Decision 9 step 5 |
| RA-54 | Low (raised as medium) | The contour rule's finer sampling is the refinement that already exists | Fixed | Decision 10 step 3, stage 1's contour check, the existing-systems table |
| RA-55 | Medium | Indexed draws were welded to a one-time format change with unstated needs | Fixed | Decision 1 (the triangle counters row), decision 11 step 6, stage 3's indexed-draw check, what would change the answer |
| RA-56 | Medium | The motion program named no compiler | Fixed | Decision 12 step 1, the existing-systems table; a cross-reference in the visibility plan |
| RA-57 | Low (raised as medium) | Record formats were fixed "once" without deciding the render origin | Fixed | Decision 11 step 5, stage 3 |
| RA-58 | Medium | The shading matrix's pass rule cannot choose while every column misses the bar | Fixed | Decision 13 step 1, the owner's call 2; a note in the author-shader plan |
| RA-59 | Low (raised as medium) | A source on the temporal history was owed before the history changes | Fixed | Decision 5's risks, decision 13's risks, stage 1's colour check, research still owed |
| RA-60 | Low (raised as medium) | The measure and rate tiers for nested views already exist in the view graph's host | Fixed | Decision 14 step 1, the looks table, the existing-systems table |
| RA-61 | Low (raised as medium) | One row of the looks table was wrong | Fixed | The looks table; an entry in open items |
| RA-62 | Low (raised as medium) | The window's exposure was described as a design the register rejected | Fixed | Decision 5 (a window carries part of its world's exposure), the looks table |
| RA-63 | Low (raised as medium) | One table cell reopened a deferred hook | Fixed | The looks table's tone curve row |
| RA-64 | Low (raised as medium) | What an image holds already has a vocabulary | Fixed | Decision 5 (every image declares what it holds), the existing-systems table |
| RA-65 | Low (raised as medium) | Machine-rate playback did not restate the video source's contract | Fixed | Decision 15 step 4 |
| RA-66 | Low (raised as medium) | The display row restated seams that exist | Fixed | Decision 1 (the present row), decision 15 steps 1 and 2, the existing-systems table |
| RA-67 | Low (raised as medium) | A present-timing extension does not by itself give a display time | Fixed | Decision 15 step 1, research still owed |
| RA-68 | Low (raised as medium) | Owed research had no place in the build order | Fixed | Research still owed, stage 0's needs, decision 15's evidence |
| RA-69 | Low (raised as medium) | The question of one store across GPU vendors had no owning stage | Fixed | Decision 10 step 6, stage 3's store check, research still owed |
| RA-70 | Low (raised as medium) | One mesh store would change what the register says a mesh's identity is | Fixed | Decision 10 (the paragraph after step 6) |
| RA-71 | Low (raised as medium) | A cheaper gradient was not weighed for the largest frame-thread cost | Fixed | Decision 2 step 4 |
| RA-72 | Low (raised as medium) | The recipe records no clock or power state | Fixed | The build order's reading conditions, stage 0 |
| RA-73 | Low | A scheduler that lives on the GPU has precedent | Fixed | Decision 4's unknowns |
| RA-74 | Low | The eye-centred cascade memo came after work built on three named cascades | Fixed | Decision 6 steps 1 and 4, stage 1 |
| RA-75 | Low | Thinned ground cover named no existing system | Fixed | Decision 8 step 5, stage 4's populations check |
| RA-76 | Low | The eye-in-a-medium step did not name the water plan | Fixed | Decision 14 step 6 and "what it rests on"; a cross-reference in the water plan |
| RA-77 | Low | One deck was quoted for grouping and against clusters | Fixed | Decision 11 step 8 and its evidence |
| RA-78 | Low | The smaller visibility buffer's gain needs several samples a pixel | Fixed | Decision 11 step 8 and its evidence |
| RA-79 | Low | The second-queue figure lacked its context | Fixed | Decision 4 (option 4f) |
| RA-80 | Low | Mesh shaders were classed as missing on hardware that has them | Fixed | The table of outside techniques |
| RA-81 | Low | Three figures under decision 7 lacked their conditions | Fixed | Decision 7's evidence |
| RA-82 | Low | The source counts did not reconcile | Fixed | How the research was checked, the sources |
| RA-83 | Low | Two documentation sweeps were deferred | Fixed | The looks section's closing paragraph; open items, the rendering skill and the mesh-bake reference |
| RA-84 | Low | Removing a listed duplicate hinged on a timing | Fixed | Decision 13 step 3 and its risks, the duplicates table |
| RA-85 | Low | Two thresholds lacked motion or a device | Fixed | Decision 6 step 3, decision 13 step 3, stage 1 |
| RA-86 | Low | "Systems that shade below full rate also denoise" was unsourced | Fixed | Decision 13's risks and evidence |
| RA-87 | Low | Probe hits are not kept in the store | Fixed | Decision 7 (the option not taken yet) |
| RA-88 | Low | The shadow role resolver has a base in the allocator | Fixed | Decision 6 step 1, the existing-systems table |
| RA-89 | Low | Compaction and the pyramid named no existing kernels | Fixed | Decision 11 step 4, decision 6 step 3, the duplicates table |
| RA-90 | Low | The fitted window extends a fit that exists | Fixed | Decision 14 step 2, the existing-systems table |
| RA-91 | Low | The overlay keeps its own copy of a glyph atlas | Fixed | Decision 15 step 3 |
| RA-92 | Low | Two plans target the collapse's share of the bake | Fixed | Decision 10 step 5; the visibility plan |
| RA-93 | Low | Edits to a scatter had no stated effect on harvested state | Fixed | Decision 12 step 2, stage 4's populations check |

### Every finding from the second review

| Finding | Severity | What it found | Outcome | Where the page changed |
|---|---|---|---|---|
| R2-01 | High | Stage 2 asked caster culling to deliver what only compaction can | Fixed | Decision 11 steps 1 and 4 and its risks, stage 2 (the draw-list and caster-culling checks). Same problem as RA-24 |
| R2-02 | High | Removing the hidden arrival render needs the minimums that replace it | Fixed | Decision 2 step 2, decision 14 step 1, stage 2's capture check. Same problem as RA-14 |
| R2-03 | High | A caster mask built for one frame is used for a shadow map shown for several | Fixed | Decision 11 step 2 (how long it holds), the invariants, stage 2's caster-culling check |
| R2-04 | High | One total of optical depth cannot answer for a receiver between two panes | Fixed | Decision 6's risks and unknowns, the table of outside techniques |
| R2-05 | Medium | One row pooled three GPU features and misstated which devices have them | Fixed | The table of outside techniques. Same problem as RA-80 |
| R2-06 | High | The window exposure claim reversed a recorded decision | Fixed | Decision 5 (every image declares what it holds; a window carries part of its world's exposure), the looks table, stage 1's colour check. Same problem as RA-62 |
| R2-07 | High | The reported GPU occupancy was divided by the wrong thing | Fixed | Decision 1 (the GPU time row), decision 4 (the problem, the "why", the unknowns) |
| R2-08 | High | An average GPU budget cannot protect one presented frame | Fixed | Decision 4 step 1 and its risks, stage 1's lighting check, the owner's call 5 |
| R2-09 | High | A uniform sky gain is not always a pure scale after capture | Fixed | Decision 7 step 3 (the sky's gain; a local light's brightness), its seven comparison cases, stage 2's lighting check |
| R2-10 | Medium | Shared vertices cannot carry a different facing for each triangle | Fixed | Decision 14 step 6, decision 11's risks, stage 4's windows-and-glass check |
| R2-11 | Medium | Stage 0 could finish without several readings that rank the work | Fixed | Decision 1's table, stage 0's done list. Same problem as RA-35 |
| R2-12 | High | The mesh under audit chooses where the coverage check looks | Fixed | Decision 8 step 4 and its risks, decision 10 (how a contour fix is judged), the invariants, stage 3's chain check, what would change the answer |
| R2-13 | High | A floor kept in memory cannot cover a place whose floor does not exist yet | Fixed | Decision 9 step 4, the invariants, stage 3's residency check |
| R2-14 | High | One present id cannot join attempts, skips and replaced swap chains | Fixed | Decision 15 step 1 and its risks, the invariants, stage 4's admission check, what would change the answer |
| R2-15 | High | A change of recipient must take effect where a picture is delivered | Fixed | Decision 14 (a changed key is not enough on its own), the invariants, stage 4's recipients check |
| R2-16 | Medium | The shader matrix's failure branch named no workable path | Fixed | Decision 13 step 1 and its risks, stage 3's shading check, the owner's call 2. Same problem as RA-58 |
| R2-17 | Medium | The population proposal left out the consumer that already exists | Fixed | Decision 12 (the problem and step 2), stage 4's populations check. Same problem as RA-19 |
| R2-18 | Medium | A fitted window needs one mapping for every reader of its image | Fixed | Decision 14 step 2, stage 4's windows-and-glass check |
| R2-19 | High | A cap on probes does not bound the GPU work list | Fixed | Decision 4 step 4 and its risks, the invariants, stage 2's background-work check |
| R2-20 | High | Factoring the direct lights also needs the bounce factored | Fixed | Decision 7 step 3 (the bounce is stored per group too), its risks and comparison cases |
| R2-21 | Medium | Query traffic does not size the visibility store | Fixed | Decision 1 (the shadow-queries row), decision 7 step 6 and its risks, the combinations list |
| R2-22 | High | The fade slots do not see a change of role | Fixed | Decision 6 step 1 and its risks, stage 1's shadow check |
| R2-23 | Medium | Boxes for blended draws cannot ride on the evidence for rigid ones | Fixed | Decision 11 step 3 and its risks, stage 2 |
| R2-24 | Medium | Baking the largest chunks first can put unseen work ahead of what the player sees | Fixed | Decision 10 step 5, stage 2's bake check |
| R2-25 | Medium | The permanent rejection of attribute quadrics rested on a false premise | Fixed | The table of outside techniques, decision 10 step 5 |
| R2-26 | Medium | The history's bandwidth saving assumed a full, still window | Fixed | Decision 13 (the problem, step 2, the "why", the risks), stage 4's history check |
| R2-27 | Medium | One probe lookup for a view cannot serve glass automatically | Fixed | Decision 13 step 3, stage 4's history-and-lookup check |
| R2-28 | Medium | A clear of only the glass tiles must know the tiles before drawing | Fixed | Decision 14 step 5, decision 6 step 6, stage 4's windows-and-glass check |
| R2-29 | Medium | Routed sessions share residencies too | Fixed | Decision 1 (the residency row), decision 11's risks, stage 3's table check |
| R2-30 | High | Stable lighting can pass with a trace mesh that is consistently wrong | Fixed | Decision 7's risks, stage 3's lighting-tier check |
| R2-31 | Medium | The memory-release check needs a case where release is possible | Fixed | Decision 9 (the eviction paragraph), stage 3's residency check |
| R2-32 | Medium | Different resource lifetimes were listed as one duplicate | Fixed | The duplicates table, decision 4 step 5 |
| R2-33 | Medium | Knowing the eye is inside a medium does not give the water's path length | Fixed | Decision 14 (the problem and step 6), stage 4's windows-and-glass check. Related to RA-76 |
| R2-34 | Medium | A tick cadence does not prove every tick's frame was uploaded | Fixed | Decision 15 (the problem and step 4) |
| R2-35 | Medium | Stage 4 could pass without looking at several things it ships | Fixed | Decision 15 (the problem, step 6, the risks), stage 4's overlay and Steam Deck checks |
| R2-36 | Low | The terrain research gap is narrower than the page said | Fixed | Decision 12's risks, research still owed |
| R2-37 | Medium | The cited line shows the largest scale, not the nearest copy | Fixed | Decision 8's problem, stage 3's chain check |
| R2-38 | High | Marking receivers cannot depend on the shadow map it is about to produce | Fixed | Decision 11 step 2 (what it covers), the invariants, stage 2's caster-culling check |
| R2-39 | Medium | Indexed draws were an experiment in one place and compulsory in another | Fixed | Decision 11 steps 5 and 6, stage 3, what would change the answer. Same problem as RA-55 |
| R2-40 | Medium | One slowest-frame comparison can pass a regression and fail a gain | Fixed | The build order's rule on how a change is judged. Related to RA-04 |
| R2-41 | Medium | The colour checks left out a display output and did not define the patch | Fixed | Decision 5's risks, stage 1's colour check. Related to RA-31 |
| R2-42 | Low | What Ju's paper calls experimental is one threshold | Fixed | Decision 10's evidence and unknowns |

## Sources

The decisions cite a source by its first author or its product; each list
below gives the title and the address it was read at. A source is listed as
confirmed when the verification pass found in it at least one quotation this
page uses. That is a statement about the quotation: nine of those sources
also carried a claim the pass narrowed, and the narrowing is stated where
the claim is used.

The 63 outside claims the pass checked come from 35 sources. 27 are in the
confirmed list. Two more had a quotation confirmed that no decision on this
page uses, and they are listed separately. Four are partial. The last two
carry a confirmed claim about an overview or an abstract only, and stay in
the list of sources that were read and not checked: Gyrling's talk on
fibers and Carrera's paper on contouring signed distance data.

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
- Remedy at REAC 2024: <https://enginearchitecture.org/downloads/REAC_2024_Remedy.pdf>
- "Nanite Foliage", Epic: <https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-foliage>
- GPU Gems 3, chapter 16, "Vegetation Procedural Animation and Shading in Crysis": <https://developer.nvidia.com/gpugems/gpugems3/part-iii-rendering/chapter-16-vegetation-procedural-animation-and-shading-crysis>
- "Reduce latency with DXGI 1.3 swap chains", Microsoft (archived): <https://learn.microsoft.com/en-us/previous-versions/windows/apps/dn448914(v=win.10)>
- "Advanced API Performance: Swap Chains", NVIDIA: <https://developer.nvidia.com/blog/advanced-api-performance-swap-chains>
- The `VK_EXT_present_timing` and `VK_KHR_present_wait2` proposals: <https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_present_timing.html>, <https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_present_wait2.html>

**Confirmed, and used by no decision.** The pass found the quoted words, and
this page does not rest on them.

- meshoptimizer release notes: <https://github.com/zeux/meshoptimizer/releases>
- Bevy pull request 17413, two-phase occlusion culling: <https://github.com/bevyengine/bevy/pull/17413>

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

**Opened by a later review's reader.** This page's verification pass did not
reopen them, and each is used as that reader's account.

- Guennebaud, Barthe and Paulin, soft shadow mapping with a hierarchical shadow map, EGSR 2006: <https://www.labri.fr/perso/guenneba/docs/SoftShadowMapping-egsr06.pdf>
- The `VK_EXT_present_timing` reference page: <https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_present_timing.html>
- Peters and others, on transmittance to each surface, I3D 2018: <https://momentsingraphics.de/I3D2018.html>
- Kubisch, "Introduction to Turing Mesh Shaders", NVIDIA: <https://developer.nvidia.com/blog/introduction-turing-mesh-shaders/>
- The Mesa 23.0 release notes: <https://docs.mesa3d.org/relnotes/23.0.0.html>
- Lengyel's transition cells: <https://transvoxel.org/>

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
