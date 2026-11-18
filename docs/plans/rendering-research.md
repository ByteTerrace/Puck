# Rendering research

This page is the plan that came out of a research round on Puck's renderer.
It says what is wrong today, what we propose to build, and how the work is
split into lanes that run at the same time. A session that has only this
repository can dispatch [the first wave](#the-first-wave) from this page.

Nine research passes each read one area of the renderer's code, collected
what had been measured there, and judged published work against it: the
mesh bake, indirect light, shadows, visibility, shading, nested views, GPU
scheduling, foliage and terrain, and presentation. Reviews then read the
result against the code. The reviews ran nothing. The one measurement taken
since is the quiet benchmark run the summary reports.

Three things live elsewhere:

- **The step designs.** Each decision here gives the problem, the proposal,
  the reason, the cost and what is unknown. The steps in full, with their
  checks, are in [the step designs](rendering-research/step-designs.md).
  A lane that takes up a decision moves its steps into the plan that owns
  them, which every decision names.
- **The review history.** Each review's findings and the answer to each
  are in this file's history (`git log --follow
  docs/plans/rendering-research.md`).
- **The queue.** [Open items](open-items.md#next) ranks this work against
  everything else.

## Summary

### What is wrong today

One reading exists at the size the bar is read at: a quiet run of the
benchmark's `debug` route on the RTX 4070 at 2560 by 1440, on both backends,
with the world's authority in the same process. The figures below are from
its second loop, which starts with every mesh already baked.

- **A typical frame takes twice its time, on the CPU and on the GPU.** The
  frame thread's work in one loop iteration has a median of 33 ms on Direct3D
  12 and 37 ms on Vulkan, where 60 frames a second allows 16.7 ms. About 7 to
  10 ms of it is the simulation step and about 26 ms is building the frame.
  The main view's GPU passes add up to about 25 ms on both backends. Every
  iteration of the run missed the bar.
- **One GPU pass is two fifths of the main view.** The pass that shades each
  pixel (`sdf.world$views`) takes 10.5 ms on Direct3D 12 and 13 ms on Vulkan.
  The next largest are the two sun shadow maps (5 ms together on Direct3D 12,
  2.6 ms on Vulkan), the antialiasing resolve (3 ms), and the interface
  overlay with the benchmark's panel up (2.8 ms and 2.4 ms).
- **The slowest frames are single simulation steps.** In the lift chapter a
  dozen steps take 0.6 to 0.8 seconds each on both backends, which puts
  that chapter's slowest 1% of frames at about 0.8 s. On Direct3D 12, 18 of the 20
  iterations over 100 ms are led by the step. No iteration was mostly a
  garbage-collection pause.
- **Vulkan allocates eight times what Direct3D 12 does.** Direct3D 12
  allocates about 5 MB in an average iteration and spends 1% of the loop
  paused for collections. Vulkan allocates about 41 MB and spends 5%. A lane
  is already working on Vulkan's allocation
  ([lanes in flight](#lanes-in-flight)).
- **Indirect light follows a lighting change late.** Lighting work is granted
  for each rendered frame, so at the 20 or so frames a second the route runs
  at, the lighting gets a third of the work it would get at 60. The automatic exposure
  meters stale light, so night can be exposed as day.
- **Lighting and shadows look wrong almost everywhere, and three causes
  are defects.** Linear light values are shown as if they were already
  encoded for the display. A shadow's softness jumps about seven-fold
  where the near shadow map hands over to the far one, and a moon that
  emits no light holds a shadow map all day. Views through portals and on
  screens get no shadow maps and no ambient occlusion at any size.
- **A change to the bake's code costs a whole bake.** An edit to a creation
  rebakes only that creation. An edit to the code that bakes rebakes every
  mesh, which takes just over a minute. Six of the fourteen blend exhibits
  fail the mesh audit.
- **A mesh that has not arrived is a hole in the picture, and mesh memory is
  given back only when there is spare memory to do it with.**
- **Nothing has been read on the weaker devices or against a remote
  authority.** The benchmark cannot yet drive its route when the authority is
  in another process.

### What we propose

1. **Run the work as lanes side by side, not as stages.** Twelve lanes can
   start now and run at once ([the first wave](#the-first-wave)). Each has an
   outcome, the files it owns, and a finish line it reaches by building what
   it touched and running it in the debug world. A second wave follows, and
   each of its lanes names the landing that unlocks it. An item waits only
   for something it truly needs.
2. **Aim first at what the reading names.** The shading pass, the lift's
   slow steps, the 26 ms of frame building that has no breakdown, and the
   antialiasing history, which is also the largest store after the meshes.
3. **Fill the engine gaps first.** A gap is a limit in the engine that
   stops a place reaching its look: terrain that bakes too coarsely and has
   no rock treatment, no shadow past 120 m, relief that buries anything set
   into it, and water whose colour ignores its depth. Fifteen are listed,
   ranked by what each blocks
   ([engine gaps, filled first](#engine-gaps-filled-first)).
4. **Measure beside the work, never ahead of it.** A lane lands when its own
   evidence holds. Timing questions go to the lead's quiet window and hold no
   landing ([how the work is run](#how-the-work-is-run)).
5. **Then the shared structures that scale:** one table of instances
   tested once for every pass, and one chain of meshes per shape from fine
   to coarse, each measured against the authored shape.
6. **Hold every decision to a budget.** [The budget
   table](#the-bars-and-the-budget) splits the RTX 4070's frame and caps
   each store on the device. Its shares are provisional. The other devices
   get quality tables authored to fit, and their code paths are written as
   if the hardware were on hand.

### What we are unsure of

- What the 26 ms of frame building is made of, and how much of it is the
  frame thread waiting for a GPU that needs 25 ms for the main view alone.
- Why the lift's steps take most of a second. A thread trace of the headless
  chapter pointed at contact for rigid bodies and at sending every session a
  whole frame of state when a door swaps.
- What a frame costs when the authority is remote. Nothing has measured it.
- Whether the cold bake is limited by its processors or by waiting on the
  GPU.
- What it costs a weak device to turn a received program into a first
  surface. An MMO client depends on that number and nothing has measured
  it ([the fifth bet](#the-bets)).
- Eight designs rest on something no engine we read does. Each is a bet
  with a number that ends it ([the bets](#the-bets)).

### What the owner settled

The owner's four questions about what the engine does are answered
([what the owner settled](#what-the-owner-settled-1)): how a shared product
is named and trusted, the memory caps, render scale, and who draws a world
for a viewer that may not receive its data.

No look value was among them. The tone curve, exposure, a light's softness,
the look of night and the quality tables are authored values over a neutral
engine default ([looks are authored](#looks-are-authored)).

One design question is open. Whether the simulation step may run on a
thread of its own is not ruled out, and the job-system plan owns it
([decision 3](#3-shorten-the-tick-and-settle-where-it-runs)).

## Words this page uses

| Term | What it means here |
|---|---|
| Tick | One step of the simulation. The debug world ticks 30 times a second. |
| Frame thread | The one thread that runs the tick and then builds the frame (`HostLoop.Run`). |
| Produce | Everything the frame thread does to build one frame and hand it to the GPU. |
| View | One rendering of a world from one camera. A nested view is shown inside another: through a portal or a window, or on a screen in the world. |
| Residency | The GPU-side copy of one world: its instance tables, baked meshes and lighting data (`SdfWorldResidency`). |
| Capture | Reading a world's poses and state for one frame and packing them for the GPU. Preparing a residency captures it. |
| Bake | Puck's shapes are authored as signed distance fields. The bake samples a field and builds a triangle mesh from it. A cold bake starts from an empty mesh store. |
| Tier, level | A tier is a mesh built from the field on a coarser grid. A level is a simplified copy of one tier's mesh. |
| Floor | A shape's coarsest acceptable tier, kept in GPU memory so there is always something to draw. |
| Cascade | One of the shadow maps a directional light uses: a near one, a far one, and one for a second light. |
| Probe | A point in a 3D grid that stores the indirect light arriving there. Tracing a probe finds which surfaces its rays hit. Relighting it shades those hits with the current lights. |
| Kernel | A compute shader: a program the GPU runs over many pixels or records at once. |
| Bar devices | The RTX 4070 and RTX 2060 at 60 frames a second (16.7 ms a frame), and the Steam Deck at 45 (22.2 ms). |
| Route, chapter | The benchmark plays a scripted walk through the debug world. A chapter is one named leg, reported on its own. |
| Lane, wave | A lane is one agent's piece of work on its own branch, with an outcome and a finish line. A wave is a set of lanes that run at the same time. |
| The lead | The session that dispatches lanes, merges their branches and runs the shared machine's GPU and measurement windows. |
| The register | The record of settled rendering decisions ([decisions](../decisions/rendering.md)). |

## The bars and the budget

The owner has ruled how the frame bar is read
([AGENTS.md](../../AGENTS.md#puck-is-pre-release), "Upper hardware
enhances"):

- **At what size.** 2560 by 1440 on the RTX 4070, 1920 by 1080 on the RTX
  2060, and the Steam Deck's own panel in its native configuration.
- **In both topologies.** With the world's authority in the same process,
  and with a remote authority on the Azure deployment. In the first the
  frame thread pays for the tick. In the second it pays to receive and
  apply what the authority sends.
- **On the RTX 4070 first.** The work meets both bars there before
  anything is tested on the RTX 2060, the Steam Deck or an HDR display. No
  fundamental work waits for that hardware. The other devices' paths are
  still written now: a quality table authored for each device, and a
  memory bar for each device.

So "highest quality" on a device means that device's authored table. The
standard package authors the three tables, and any world can override
them. The engine's work is the mechanism that selects a table by device.

The table below splits the RTX 4070's frame so each decision can say
which part it moves and by how much. Its figures are a forcing function,
in the sense of the owner's velocity rulings: they keep the work honest
and make a regression visible, they are not finish lines, and a change is
judged by what it does for a player moving through the world, never by
whether it crosses a row.

The measured column is one quiet run of the benchmark's `debug` route, six
chapters, at 2560 by 1440 with the authority in process. Each cell gives
Direct3D 12 first and Vulkan second, from the run's second loop. Times for
the frame thread are medians over the loop's iterations. Times for a GPU
pass are the mean of the pass's rolling average over the six chapters. The
run used the render settings the debug world boots with, because the
benchmark does not apply a device's quality table yet. The first wave's
`bench` lane adds that, and the column is read again at the RTX 4070's
table then. One run sets no baseline: the column says where the time is,
and a change is judged by its own before and after.

The frame thread and the GPU work at the same time, so each has to fit the
16.7 ms on its own. The two groups of rows are budgeted separately.

| On the frame thread | Measured at 2560 by 1440 | Provisional share | What the share assumes |
|---|---|---|---|
| The whole iteration | Median 33 ms and 37 ms. The slowest 1% start at 75 ms and 147 ms. The slowest are 0.87 s and 0.93 s | 16.7 ms | The bar |
| Tick, authority in process | Median 7.1 ms and 9.7 ms an iteration, or 6.5 ms and 8.1 ms a step. A dozen steps in the lift chapter take 0.6 to 0.8 s | 4 ms | The lift's steps are a defect, fixed at its cause in the first wave. The ordinary step then falls by its serial fixes and the parallel tick (decision 3); see the note on world count |
| Applying a delivery, authority remote | Not measured. The route cannot be driven against a remote authority yet | 1.5 ms | The first wave builds that driver, and its first run sets this row |
| Produce | Median 26 ms on both. The slowest 1% start at 56 ms and 92 ms | 9 ms | Decision 2: only the residencies a frame reads are captured. How much of the 26 ms is waiting for the GPU is not known, and the first wave splits it. Recording several views in parallel is on hold and is not counted here |
| Margin | None | 3.7 ms | |

| On the GPU | Measured at 2560 by 1440 | Provisional share | What the share assumes |
|---|---|---|---|
| The main view, all passes | 24.8 ms and 25.0 ms | 13.2 ms | The sum of the six rows below |
| Drawing the meshes (`sdf.world$mesh`) | 1.3 ms and 1.1 ms | 1.2 ms | No change claimed |
| Resolving what each pixel shows (`sdf.world$surface`) | 1.6 ms and 1.5 ms | 1.5 ms | No change claimed |
| Shadow maps, far and near | 5.0 ms and 2.6 ms | 2.5 ms | Shadow casters culled by the surfaces that read them (decision 11), and maps given by role (decision 6) |
| Shading (`sdf.world$views`) | 10.5 ms and 13.0 ms | 5.0 ms | The pass has to more than halve. The probe lookup, about a third of it at a smaller window, moves to once for each view at its own rate (decision 13). Nothing yet claims the rest, which makes this the row most likely to move |
| Composite (`sdf.world$composite`) | 1.6 ms and 2.0 ms | 1.5 ms | Glass costs what the visible glass covers (decision 14) |
| Antialiasing resolve, bloom and encode | 3.0 ms and 3.1 ms for the resolve | 1.5 ms | Decision 13's split history stops the resolve copying the stored window every frame |
| The interface overlay | 2.8 ms and 2.4 ms with the benchmark's panel up | 0.5 ms | Decision 15: the overlay as clipped geometry |
| Nested views | 1 to 5 ms for each nested view on a frame it renders | 1.5 ms | One nested view in frame; see the note on views |
| Background work (lighting, bake, uploads) | An allowance of 4.2 ms, of which the share used is unknown | At least 1.0 ms | Decision 4: the allowance follows the GPU time the frame leaves free |
| Margin | None | 0.5 ms | The claimed gains reach the bar with almost nothing to spare, so a row that misses its share has to be made up in another |

| Store on the RTX 4070 | Today | Provisional cap | What the cap assumes |
|---|---|---|---|
| All GPU memory | Not measured as one figure. The main view's graph holds about 2.5 GB at the bar's size | 8 GB of the card's 12 | Room for the system and other programs (Puck plays nice with its host). The cap is not an engine input today: the limit the engine admits against is the operating system's budget less a tenth (`MemoryAccount.Record`). The first wave makes it one |
| Resident baked meshes | About 3 GB, growing toward 4 GiB | 3 GB resident around the viewer | Residency evicts by distance and view; the cap is independent of how large the world is |
| Image history | 196 bytes a pixel in two copies, about 1.4 GB for the main view at 2560 by 1440 | 0.9 GB for the main view | Decision 13, which also gives the arithmetic. A nested view that keeps a history adds its own at its own size, and the lane that splits the history reports that total |
| Lighting probes | About 72 MB a table in up to four copies, and a hit cache of about 90 MB, for every world shown | 0.6 GB in one shared pool | One pool for every world in view, not a pool per world |
| Shadow maps | 96 MiB for the cascades, and 144 MB of glass storage for each shadowed view at the highest tier | 0.4 GB | Glass storage shared across views |
| The bake's own memory while baking | More than 10 GB of the process's private memory in a cold bake | Bounded on every device. On the Steam Deck it counts inside the 10 GB for the process and the GPU together | A walk into a place whose meshes are not stored bakes during play, so this is a play-time cost. The bake's peak has to fall far enough that every device can bake a floor (see [how a shared product is named and trusted](#how-a-shared-product-is-named-and-trusted)) |

Notes on reading it.

- **Each cap is the device's, and views share it.** One process on one card
  has one cap. It bounds what is resident around the player, and it never
  follows a world's size or the number of worlds in view: at the scale Puck
  is built for, the world a player stands in is unbounded and many worlds
  can be in view through portals. Two seats on one machine share the one
  cap. A store whose size follows world size or world count is a defect
  against this table, whatever its total today. A cap also covers the
  moments that hold more than the steady state: jobs in flight, and the
  second copy that exists while a store is replaced.
- **Views and worlds in frame.** The nested-view row and the tick row
  assume one nested view and today's few worlds. Each extra view in frame
  and each extra world stepped costs more, so the first wave measures the
  cost of one more view and one more world, and the design states how that
  cost is bounded (by screen coverage, by distance, by a cap on views
  stepped at full rate). Every world instance stepping in series on the
  frame thread (`WorldInstanceHost.StepInstances`) does not scale to many
  visible worlds; the [job-system plan](engine-job-system.md#open-questions)
  owns that question, and decision 3 says how it is asked.
- **Background work under overload.** The background row is a promise of
  service, and a promise needs a rule for when it cannot be kept. While the
  frame's own passes leave at least the row's share of the period free,
  lighting receives the work its authored age needs each second. When they
  do not, the frame keeps its time: background work is cut to what is left,
  the lighting's age grows, and the report shows frames that missed behind
  a background job and lighting that is overdue as two separate counts.
  Only a consumer's minimum (decision 2) is served regardless, in jobs cut
  to one frame's allowance.
- **The other devices are not given milliseconds here.** Under the owner's
  ruling their testing waits until the RTX 4070 meets its bars, and their
  hardware differs too much for borrowed figures to mean anything: the
  Steam Deck's GPU has a small fraction of the RTX 4070's throughput. Each
  device's quality table (its window, shadow tier, probe tier and detail)
  is authored so that its counted work fits a share of its frame. Selecting
  a weaker device's table on the lead machine checks two things: that the
  table selects what it says, and what work it submits for a given view
  (draws, vertices, shaded pixels, shadow texels and dispatches), which is
  the same on every machine for the same view and the same schedule. It
  cannot certify that device's time. What a moving frame submits also
  depends on timing: the background budget accrues by wall time
  (`SdfGpuWorkBudget`), and a view's refresh rate follows the display
  (`RenderGraphScheduler`). So the route's counts are read as ranges, and
  a device's own run is what finally reads its bar. The memory bars for the
  RTX 2060 (4.5 GB of its 6) and the Steam Deck (10 GB for the process and
  GPU together, the streaming plan's figure) are
  [settled by the owner](#what-the-owner-settled-1).

Each decision below states what it claims against this table. Three of the
fixes add GPU work: an encode pass for a single view, shadows for nested
views, and a probe lookup where no shadow map reaches. Each of those lands
with its measured GPU cost beside it.

## What Puck's renderer is

The thesis was written after the build order, and the first three stages
would be the same without it. Its use is for the later stages: it says
which existing mechanism each decision should extend, and it names the
bets.

In one sentence: Puck's renderer turns authored programs into pixels, and
treats everything in between as a cache that knows what it was made from,
how far it is from its source, and what it costs. Four principles follow.

### The four principles

**1. The authored field is the reference, and everything drawn is a
measured copy of it.** A shape is authored as a distance field, which is a
program. A baked mesh, a coarser mesh and a probe's hits are all derived
from that program. Because the program is still there, each derived
product can be measured against it, in both directions, and can record
that error in a unit a consumer turns into pixels or shadow texels. Part
of this exists: the bake already bounds a simplified mesh against the
field and finds lost parts (`SdfMeshFieldLevels.Measure`), though no
decision reads that record yet. An engine that starts from triangles
measures a coarse mesh against a finer mesh, and cannot ask what the
simplification erased. Not everything has a field behind it. An author can
supply a triangle mesh, and an emulated machine supplies frames. Those are
identified by their real inputs and are not measured against a field.

**2. Work follows change.** A frame should cost what changed since the
last frame, plus what a viewer newly sees. Puck can know what changed
because its simulation is deterministic and presentation reads state
through one path. Today a view still learns that nothing changed by doing
all the work: `SdfWorldResidency.IsUnchanged` prepares the residency
first and compares a hash afterwards. Two things keep this true while the
world moves. Inputs that change all the time are kept out of caches that
hold structure: a moving sun should relight probes, not trace them again.
And an authored program's bounds say how far its change can reach: a
motion program's swept box bounds where a moving part can be.

**3. A frame is demand from the display, flowing through worlds.** No
world is the root for good. On each display one world is drawn first,
because its capture places the cameras (`RenderGraphRuntime.Root`), but
which world that is changes as the player moves, and the same world is a
nested view on another display. The display asks for a view of a world at a
size, by a deadline. That view asks for others: a portal, a painting, an
emulator's screen. Each is served what its consumers are owed first, and the rest is
divided by the pixels each one presents. Demand is also predicted: as a
player approaches a portal, the far side prepares what the player is
already allowed to see of it.

**4. Results cross between worlds under each world's rules.** A world
chooses where its simulation runs. It can also choose where its derived
products come from, and what each viewer may receive. A viewer's machine
derives only from what it was delivered, so drawing and hidden
information follow one rule: at a card table, another seat's face-down
hand cannot leak through a shadow or a reflection, because its inputs
never reached the spectator.

### Three ways worlds compose

A world is the unit of authority, identity and disclosure. The unit of
rendering is the view. Worlds reach a view in three ways.

- **Nest.** A world in another space is another view at another size:
  behind a portal, or on a screen.
- **Merge.** Worlds in the same space feed one view's table of instances
  as rows: two adjacent zones, or a character standing in a zone. Adjacent
  zones are not merged today. The band of a neighbour's geometry that
  crosses a border holds at most eight placements and eight bodies
  (`WorldAdjacencyGeometry`), and the debug world authors no adjacency.
- **Publish.** One world hands another a finished product: scenery a
  neighbour places in the near world, the pictures a viewer gets when it is
  allowed only pictures, the light leaving an opening, and a machine's
  frame. The receiver may use what it was handed. A room takes its glow
  from a published screen, and a hall lights its floor with the light an
  opening publishes. It gains nothing of the publisher's scene behind the
  product.

Publication also settles who draws a world for a viewer that may not
receive its data: a casino screen showing a private table, a spectator's
window, a portal into a zone the viewer is not cleared for. The viewer's
machine cannot draw what it was never sent, and a server that draws one
picture for every observer does not survive MMO scale (two hundred people
watching thirty screens is six thousand renders). The owner settled the
model as three tiers, chosen by the author for each portal or screen, with
per-observer server rendering never among them.

- **Published pictures, the floor.** The publishing world's host renders
  each declared camera once, for a declared audience, at an authored rate,
  and every observer receives the same image. A casino screen is one
  render however many watch it, as the lift's displays are one machine
  mirrored to every screen. It gives up parallax: the picture does not
  shift as a viewer walks past. This is built first.
- **The visible part, with secrets withheld (the target for most cases).**
  "Pictures only" is usually stronger than the real rule, which is that a
  viewer must not receive data it is not entitled to. The viewer is sent
  the part of the destination that can be seen through the opening from
  the region it can stand in, redacted cell by cell by the disclosure
  engine (card backs, not card faces; a hidden room left out), and its own
  GPU draws it with full perspective at no server cost. Per-recipient
  disclosure and portals exist; the new part is a visible set through a
  fixed opening from a region of eye positions, which a distance field
  can prove rather than estimate (`SdfFieldEvaluator.TryCertifiedLineOfSight`
  exists with no caller). This is [the eighth bet](#the-bets).
- **Layered pictures, when the geometry itself is the secret.** The host
  renders the view once per opening as colour and depth in a few layers,
  and each viewer reprojects them to its own eye, which gives parallax.
  Layered depth images are established; the opening being a fixed window
  bounds what is captured, and depth reveals only what a viewer could see
  by walking past.

All three follow change: a destination is published again only when it
changes, and a still view is a derived product keyed by its inputs. Today
only the in-process case exists, and a remote world's screens show
nothing.

### What is Puck's own, and what is borrowed

Truly Puck's own, as far as the sources we read show:

- Error measured against an analytic source in both directions, kept as a
  record that chooses between products.
- Converting a part exactly where its program has a closed form, and
  deriving a finer mesh than any stored.
- Caches keyed by what a viewer was delivered, so drawing and hidden
  information follow one rule.
- A portal crossing prepared ahead, limited to what the walker may
  already see.
- Light handed across a boundary between two authorities, with a test
  that it discloses nothing.
- Continuous change carried as proved clock spans and motion programs, so
  a moving sky sends nothing over the network.

Standard techniques that appear here under Puck's names:

| This page's idea | The standard technique |
|---|---|
| A product identified by its inputs | A content-addressed cache of derived data |
| Work follows change | Incremental computation |
| Merging worlds into one view | Level streaming |
| A floor | The coarsest page a virtual-geometry system always keeps (Nanite's root page) |
| A still nested view that keeps its picture | A render target drawn on demand |
| Light stored for each source | A small basis of light groups |
| Distance bricks for tracing | A global distance field. Puck built one once as its main picture and rejected it on measurement |

Borrowing these is right. The claim to originality rests on the first
list only.

### What the thesis was missing

Five ideas Puck is placed to try were absent, and each now has a home.

- **Send programs, not products.** The source of a shape is a small
  program, which is cheap to send. Deriving from it is what costs. The
  measure is the time from receiving a program to drawing its first
  surface ([the fifth bet](#the-bets)).
- **Compose characters from shared parts.** In a city of player
  characters nearly every look is distinct, so sharing whole looks saves
  little. Sharing baked parts and composing them as rows does
  ([the third bet](#the-bets)).
- **Let the authority decide what to send by proved visibility.** The
  field evaluator can prove a sight line clear or blocked
  (`SdfFieldEvaluator.TryCertifiedLineOfSight`), and nothing outside the
  tests calls it. An authority could use it to withhold what a viewer
  provably cannot see. This belongs to [the disclosure plan](disclosure.md),
  and its first step is a count of what the proof costs for each pair of
  viewer and body.
- **Let demand reach the simulation's rate.** A world nobody sees is drawn
  less often but still steps at full rate, in series, on the frame thread
  (`WorldInstanceHost.StepInstances`). Whether an unseen world may step
  less often is its authority's choice, and the question goes to
  [the job system plan](engine-job-system.md) with decision 3.
- **Budget in counted work.** Deterministic counts hold on every machine,
  so they can stand for a device that is not on hand
  ([the budget](#the-bars-and-the-budget)).

### Where the thesis is weak

- **It does not close today's gap.** The lead machine misses its bar now.
  The thesis shapes the fixes and replaces none of them.
- **Work that follows change saves still frames, and the bar is read on
  the slowest frame.** A quick turn in a crowd changes nearly every pixel.
  The saving is real where views stand still (unseen worlds, held
  screens), and it frees GPU time for background work. It lowers the
  slowest frame only where that frame is slow because many views render
  at once.
- **The field is a reference only where it is correct.** An authored field
  can overstate its own distance, and every audit inherits that.
- **A counted price is not GPU time.** A short shading program that keeps
  many values alive can cost more than a long one. Only measurement on
  the device finds that. And nothing yet prices every consumer in one
  unit: that step of decision 4 is a design round that has not started.
- **One unit of error is exact for geometry and a model for light.**
- **Puck pays twice: for the bake, then for the triangles.** A grass
  blade bakes to about 474 triangles where its prism has a dozen. The
  thesis holds only where the field gives fewer and better triangles than
  a hand-made mesh would.
- **A shared product is trusted for what the receiver checked, and no
  further.** Trust in the machine that made it is dropped, and so is
  sharing precomputed light, which a moving sun makes stale all day.

## How a shared product is named and trusted

*The owner confirmed this design, with the receiver-owned tolerance, the
format's meaning and the Steam Deck's local floor below, which came from
the plan's review.*

**The problem.** A baked mesh's identity today hashes the bake's own code
and each backend's compiled kernels (`SdfMeshBakeIdentity`). That is right
for a cache on one machine: any change to the baker makes the old mesh a
miss. It cannot work for products that are shared or fetched. In an MMO
every patch to the baker would make every stored product a miss on every
client, and an authority would have to derive again for every client
build and every backend. The owner has ruled that an identity is
"computed from inputs, never recorded", so a version number bumped by hand
is not available.

**What we propose.** Two names for two jobs.

- **A local cache key** stays as it is: computed from every input, the
  baker's code included. Nothing records an identity as an expectation to
  compare against. The store does write each bake's identity beside its
  entries (`SdfMeshStore.Record`), and only so that a later miss can say
  which part changed.
- **A shared product's name** says what the product means: the source
  program, the authored parameters, and the version of the product's
  format. That version covers what the bytes mean, not only their layout:
  today's mesh codec warns that the same bytes can decode to another mesh
  (`SdfMeshStoreCodec`), so a change in how normal bits are read is a new
  version even when the layout is the same. A hash of the layout cannot
  see that: a change of units, or of how a normal is read, can keep every
  field's width and offset. So the version is computed from the code that
  writes the bytes and the code that reads them. Today only the writer's
  code moves the identity, and an edit to the reader moves nothing. The
  baker's code is not part of the name.
- **Trust comes from checking the product, not from who made it.** A
  machine that receives a product, or keeps one across a patch, verifies
  it against the analytic source within **the tolerance the receiver
  needs**, never one the product declares, so a producer cannot pass a
  loose product by claiming a loose tolerance. The receiver also sets the
  coverage the check must reach and what evidence qualifies each
  attribute: geometry, normals and material ids, not distance alone. When
  verification is inconclusive, the receiver derives locally. A product
  that passes is accepted whichever baker, backend or machine made it.
- **Fetching shortens a wait; it is never the only way to play.** A newly
  authored creation can miss every store, and the register rules that a
  miss is derived again. So every device, the Steam Deck included, keeps a
  local floor bake that fits its memory account, with an authored image
  while it completes; a fetched product replaces that wait when one
  verifies.
- **A better baker re-derives only where better quality is wanted.** An
  old product that still verifies stays valid. The engine or an author
  asks for a tighter tolerance where it matters, and only those products
  are derived again. A patch never invalidates everything.
- **Nothing is bumped by hand.** A product whose format version the
  receiver does not know is never used as it stands. The receiver either
  derives, and counts it, or decodes the bytes with its own reader and runs
  the check above. That check measures the decoded mesh against the source,
  so a product that passes it is right under the receiver's reading,
  whatever wrote it. The case to exercise is a change to the reader that
  keeps the layout: a product from before it must be refused or must pass
  the check again, and is never drawn wrong.
- **A cache on one machine and an exchange between machines are two
  cases.** A machine reusing its own store across runs trusts its local
  key. The check above is for a product that came from another machine or
  outlived a patch.

**What it costs.**

- *Verification is work.* Checking a product against the field costs
  field samples. If a full check costs as much as deriving, fetching
  saves nothing.
- *A spot check does not see everything.* Checking a sample of points is
  cheap and can miss a thin feature or a part that vanished. The check
  therefore samples over the authored shape's bounds, not the product's
  own, and the receiver states the coverage it requires. Where the check's
  guarantee stops, at features thinner than its sampling, it answers
  "inconclusive" and the receiver derives. A check that samples is never
  reported as having caught every omission.
- *Some attributes have no field to check against.* An authored triangle
  mesh and a machine's frame have no analytic source. A probe's hits
  depend on the whole scene around them. Those are derived locally, or
  arrive as publications from the world that owns them.

**What it does not change.** Who may fetch a product is still the
disclosure layer's decision. A name grants no access, and a viewer that
was not delivered a shape's program cannot verify a product of it, so it
receives only publications.

**What we still have to find out.** What verification costs against
deriving on each device. How wide the difference between GPU vendors is
against a receiver's tolerance. What a floor bake costs in time and peak
memory on the Steam Deck, whose memory account covers the process and the
GPU together.

## The bets

Most of this plan is ordinary repair. Eight items are different: each
rests on something no engine we read does, and each can fail without
anyone noticing or at great expense. A bet gets a design memo and a
bounded prototype before anything is built on it. It also gets a number
that ends it, a time box for one lane, and a fault put in on purpose that
its check must catch. A bet that passes its box without a result is stopped
and reported, and a bet whose number comes in on the wrong side is ended
that day. Nothing else waits for a bet.

| Bet | Time box | When it starts |
|---|---|---|
| 1. A view that has not changed keeps its picture, decided from the change records the client already reads | Memo 3 days, prototype 7 | The memo now. The prototype only if the breakdown of produce shows that capturing a shown, unchanged world is a real cost |
| 2. A portal crossing prepared ahead | 7 days | When decision 2's minimums exist |
| 3. Worlds merged into one view, and crowds built from shared parts | Memo 4 days, prototype 10 | The memo any time. The prototype when the instance table takes rows from several worlds (decision 11) |
| 4. Products shared by a name that says what they mean | Memo 4 days, prototype 8 | Now on the desktop: the owner confirmed the design. The exchange between GPU vendors has its own box when the hardware is on hand |
| 5. A program received cold shows a surface quickly | Measurement 2 days, prototype 5 | The measurement now. The prototype when a floor exists |
| 6. Light carried across an opening between two worlds | 8 days | When light is stored for each group of sources (decision 7) |
| 7. Tracing light with the field instead of triangles | 6 days | When the probe trace has been measured against the lighting's mesh |
| 8. A viewer is sent the visible part of a world, with secrets withheld | Memo 4 days, prototype 8 | The memo any time |

### What decides each bet

Each entry says what is measured, against what, and which result ends the
bet. A result that does not fit one of these is reported as unmeasured,
never as a pass.

**1. A still view.** Two runs of the benchmark's multi-view chapter, with
the shortcut on and off. Both start from the same named queue of background
work, taken after one lighting change at a settled stop.

- *Ends it:* the slowest frame falls by less than 2 ms, and the rays traced
  and bytes uploaded in the ten seconds after the change rise by less than a
  quarter. Jobs are not counted, because smaller jobs would raise the count
  with no more work done. Frames that missed are reported apart.
- *The fault.* The shortcut keeps its own copy of each change record. One
  copy is silenced after the full decision has received the change, and the
  audit must report the stale view in that frame. Silencing the emitter
  itself proves nothing: the full decision reads the same revision
  (`SdfCompositionFrameSource`), so both answers would agree on the old
  picture.
- *What that does not show.* An emitter that never reports a change is caught
  only by a rebuild forced from the delivered inputs, with a marker that
  moves when the rebuilt program differs. The control run keeps its own
  state, so it cannot overwrite what the candidate published.

**2. A crossing prepared ahead.** One crossing, walked from a destination
whose meshes are not stored, with a fixed lead time and a fixed byte budget,
and a marker in the destination that only a correct picture shows. The
ordinary arrival policy (`WorldPortalVisibility.ArrivalShare`) is the same
in both runs, and the lead is short enough that it cannot finish alone.

- *Ends it:* with prediction on, the destination's first correct picture is
  not on screen on the frame the eye crosses, or it arrives no sooner than
  with prediction off; or the slowest frame in the second around the
  crossing is over 16.7 ms and slower than with prediction off; or, on a
  walk that turns away, more than half the bytes fetched for the prediction
  are bytes that ordinary demand did not ask for in the ten seconds after
  the turn.
- *Reported apart:* a crossing whose destination was already resident, and
  a prediction that fetched nothing. Neither is a pass.
- *The fault.* Prediction is withheld for that one destination. The check
  must report the marker missing on the crossing frame.

**3. Crowds and merged worlds.** Two hundred characters of distinct looks
from a wardrobe of 40 parts, with the tier, the bake's identity and the
starting store fixed.

- *Ends it:* more meshes are baked than there are distinct pairs of part and
  tier, plus pairs of blended seam and tier; or, between 50 and 200
  characters with the visible ones held fixed, any one of rows visited,
  bytes packed and draws submitted grows by more than a tenth. The three
  are compared separately.
- *Merged worlds are checked on their own,* because a crowd says nothing
  about them. Two adjacent worlds show one draw the neighbour admits and
  one it refuses for this viewer, and the report gives the cost of any
  extra view or pass the merge needed.
- *The fault.* A part's identity is made to include its wearer. The check
  must report bakes growing with the crowd.

**4. Products that are exchanged.** The receiver, the byte format and the
checks are fixed first
([how a shared product is named and trusted](#how-a-shared-product-is-named-and-trusted)).
The corpus is every stored group of the debug world at the tiers the route
asks for, read in each direction.

- *Ends it on the desktop:* fetching and verifying a product takes longer to
  a first usable surface than deriving it, on a stated link.
- *Ends it across vendors:* more than 1 in 100 of the corpus fails the
  receiver's own tolerance in either direction. Two graphics APIs on one
  card compare no vendors, so this rate stays unmeasured until an RTX card
  and the Steam Deck exchange a store.
- *The fault.* A product with the right name and one part removed, one
  material id swapped and one patch of normals flipped. Each is refused by a
  check that is certain to see it: the measure against the field covers
  every material region, every normal region and every part the product
  loses (`SdfMeshFieldLevels.Measure`). Where that guarantee stops, at
  features thinner than its sampling, the check answers "inconclusive" and
  the receiver derives.

**5. A program received cold.** Every creation of the debug world is
requested with an empty store.

- *What is recorded first:* the time from receiving each program to its
  first usable mesh, as the engine is today. A time to a floor does not
  exist until a floor does.
- *Ends it:* on the RTX 4070, any creation shows no real floor within ten
  seconds, or the slowest twentieth of creations take more than 2 seconds
  from program to drawn floor. The Steam Deck's target is 5 seconds. A
  median is not the test: it passes with nearly half the creations missing.
- *Reported apart:* a real floor and an authored fallback, and the first
  surface each crossing of the route needs.
- *The fault.* A program whose floor cannot be baked. The frame must show
  the authored fallback and count the failure, never a hole.

**6. Light through an opening.** A fixed camera, a fixed light in the far
world, and fixed patches on the near floor, some lit and one in a cabinet's
shadow. The quantity is the light arriving at each patch before exposure.

- *Ends it:* the samples cost more than a quarter of the GPU time of
  rendering the far scene as a second view; or at any patch the light
  differs from a full reference by more than a tenth of the reference's
  value at the brightest patch, which gives an unlit patch a denominator; or
  the cabinet's patch is not in shadow; or any published light differs
  between two deals a spectator may not tell apart.
- *What the disclosure check reads.* The light the garden actually published
  to each viewer and what the hall computed from it, for both deals, and
  again when a seat loses access while a light job is in flight. Today's
  disclosure run records console answers and state edits (`WorldDisclosureRun`)
  and has to gain these.
- *The fault.* The opening's light is made to depend on a private hand. The
  check must catch the difference. Raising the camera's exposure brightens
  the displayed floor and must leave the arriving light unchanged.

**7. Tracing with the field.** The same rays against the same geometry, for
the triangle trace and each field method.

- *Ends it:* no field method completes a third more correct rays a second
  at equal quality, with its build, its invalidation and its transfers
  included; or one false "clear" occurs. Counted steps are not the test: a
  triangle node, an interval evaluation and a distance step are different
  amounts of work.
- *The fault.* A blocker is put on a ray known to cross a box that was
  proved empty, and the proof is not withdrawn. The same ray is traced
  before and after. A picture of the whole scene can miss it.

**8. The visible part of a world.** The gallery's portals, with the eye
allowed anywhere in the room in front of each.

- *Ends it:* the visible set through an opening sends more than half the
  destination's disclosed cells; or the viewer's local draw of it costs more
  GPU time than the published picture it replaces; or proving the visible
  set takes longer than the interval at which that picture is published.
- *The fault.* A cell the viewer may not see is placed inside the opening's
  visible region, and the disclosure check must withhold it. A cell behind
  a wall must not be sent at all.

### The first bet stands on the client's records

The first bet matters most, because decision 2 leans on it and because
the obvious place to read change from is the wrong one.

That place is the authority's state arena, which records every committed
write. Presentation never reads an arena: no file in the presentation or
renderer projects
(`Puck.World.Client`, `Puck.SdfVm`, `Puck.World`) mentions `StateArena`.
A remote client never holds the authority's arena at all. So the bet is
restated on what the client already has, which holds for a colocated
client and a remote one alike.

Two records exist and are read today.

- **The state mirror** (`WorldStateMirror`) is the one path presentation
  reads state through. Every delivery names the rows that moved
  (`WorldStateStamp.MovedRows`), and a refresh reads the slots bound to
  those rows, so "a tick that moves nothing reads nothing". It also reads
  every slot whose value is still easing toward rest, on every tick, with
  or without a delivery (`WorldStateMirror.Advance`). The mirror
  exposes a revision that moves when any slot's value changes, a second
  for colours, and a per-slot answer (`Revision`, `ColorRevision`,
  `Changed`).
- **The scene's revision vector** (`ISdfSceneEmitter.WriteRevision`). Each
  part of a scene reports its own counters every frame, and the composed
  program is rebuilt when any one differs. The counters are kept apart
  and never summed, because a sum can cancel.

The bet is that these two, with a few additions, cover every input a view
reads. Its first step is a memo that lists what
`SdfWorldResidency.Prepare` reads on the client and names, for each input,
the client record that covers it: the mirror's revision, the scene's
revision vector, the tick's snapshot, the clock phase evaluated for the
presented tick, or the camera. Inputs with no record are listed as gaps.
The prototype then closes gaps in an existing revision vector. It does not
join the authority to the frame.

Three rules come with it, because its failure is silent: a view that
wrongly keeps its picture also lowers the count the work is judged by.

- The existing refusals stay outside the shortcut. No view keeps its
  picture while meshes are pending, lighting is settling or a source is
  waiting, or while a screen's image is rewritten in place
  (`SdfWorldTables.ForcesRender`).
- An audit compares each skipped view with the full decision the
  residency makes today, and counts views that were eligible, skipped and
  audited. A check that never skips audits nothing and would pass, so the
  audit requires a real skip in a still scene. It reads what was delivered
  and what was presented at the same tick, and its cases include a value
  still easing on an otherwise quiet delivery, and the sky's clock.
- Nothing forces a future reader of a new input to add a record. The
  audit is the only check for that, so the shortcut stays under
  adversarial review ([hazards](../development/hazards.md#silent-invariants)).

Checked for this section: the three projects' lack of any `StateArena`
reference, the mirror's summary and its refresh from the stamp's moved
rows, and the revision vector's contract. Not checked: that every input of
`Prepare` reaches the client through one of these. That is the memo's
job.

## The decisions

Fifteen decisions follow. Each gives the problem, what we propose, why,
what it costs or risks, and what we still have to find out. Each ends
with what was checked, and with the plan that owns its steps. "Checked"
means the code or the source was opened for this page and says what the
text says. "Not checked" covers three weaker kinds of claim, and the note
says which: a reader's report that was not reopened, a single measurement
on the lead machine that was not repeated, or arithmetic. The steps in
full are in [the step designs](rendering-research/step-designs.md).

### 1. Measure beside the work

**The problem.** Many of the gains this plan ranks are arithmetic that no
counter can confirm. One reading exists at the size the bar is read at, on
one GPU, with the authority in process.

**What we propose.** Take the missing readings while the work runs, on the
RTX 4070 at 2560 by 1440, on Direct3D 12 and Vulkan, in both topologies.
A reading ranks later work. It never holds a lane that can already show its
own result. The readings that decide the most:

- produce split by part, with the time the frame thread waits for the GPU
  kept apart from the time it works, and the tick split by phase and by
  world;
- what one more world in frame and one more view in frame cost;
- the same route driven against a remote authority, which needs a route
  driver that reads what the client was delivered (below);
- garbage collections and bytes allocated for each loop iteration, by
  source;
- GPU time for each frame, split into the main view, nested views and
  background work, and how much of its allowance the background work
  uses;
- for each attempt to present a frame, whether it was shown, skipped or
  dropped, and the time from input to display;
- what limits a cold bake, with its time and its peak memory;
- the time from receiving a creation's program to its first usable mesh;
- the time from saving an edit to seeing it, for a `.puck` document, a
  shader and C#;
- counted work for each pass, which fills the budget table's counts.

Each reading has a row in the step designs that names the step that uses
it. Nothing here changes what a player sees, and nothing is recorded as a
baseline.

**The route driver for a remote authority.** The benchmark plays its route
through an authored seat program, and the driver that steers it reads the
seat's body, the places it stands in, the shapes of the places it is going
to and the navigation mesh from the server object of the world the seat is
in (`WorldSeatDriver`). When the authority is another process there is no
such object on the client, so the driver reads nothing and the route cannot
run. A run started with `--connect` today still steps the boot world
locally and transfers the body to the remote world, so a reading taken
that way would describe the wrong topology.

The driver is rebuilt to read what the seat's client was delivered: the
body's pose from the delivered snapshot, and places and navigation from the
world document the client already holds. One driver then serves both
topologies. The run's report says, separately, which endpoint the route
played against, what the local process simulated, what delivery cost and
what rendering cost, and it carries a witness that the seat's body arrived
in the remote world. A run that is still observing the boot world does not
count as a remote reading.

**Why.** A fix ranked by arithmetic can spend weeks on a cost that turns
out small. The first reading at the bar's size already moved the order: it
put the shading pass and the lift's steps ahead of allocation.

**What it costs or risks.** Measuring is work that changes nothing a player
sees, so it is kept from becoming the bottleneck: instruments are built by
the lane that needs them first, and timing questions go to the lead's quiet
window ([how the work is run](#how-the-work-is-run)).

**What we still have to find out.** Everything in the list. The same
readings on the RTX 2060 and the Steam Deck wait until the RTX 4070 meets
both bars. Until then the other devices' tables are run on the RTX 4070 as
estimates of their counted work.

**Evidence.** Checked: how the Vulkan presenter reports timing, the window
of 32 frames behind a pass timing, that world instances step in series,
that the route driver reads the server of the seat's world, and that
`--connect` transfers the body from the boot world. The 2560 by 1440
figures are one quiet run of the `debug` route's second loop.

**Owning plans.** [The benchmark](benchmark.md) for the route and the
report, and [the job system plan](engine-job-system.md) for the frame
thread's breakdowns.

### 2. Capture and prepare only what a frame shows

**The problem.** The frame thread prepares every world it holds before
anything has decided what the frame needs. Preparing a residency captures
it: it reads poses and state and packs them for the GPU. The work is then
asked for a second time, because the question "has this view changed?" is
answered by preparing and comparing a hash afterwards. Each world that is
arriving also renders a hidden window, because only a scheduled render
moves its data to the GPU.

**What we propose.** Capture only the residencies a frame needs, in this
order within a frame.

1. *Upkeep for every residency.* Finished GPU jobs are collected and
   their callbacks run, uploads are polled, and requests to give memory
   back are answered, whether or not the residency is captured.
2. *The main world's capture*, which places the cameras. This stays first
   because nothing else can supply them.
3. *The list of what to render is sealed after the cameras are placed*,
   not before.
4. *A residency is captured only if something reads it this frame:* a view
   on screen, an upload that is owed, or a consumer's minimum. A minimum
   is work owed whatever is on screen: a predicted arrival's first image
   and lighting, a screen out of view whose glow lights a visible room,
   picking, and the lighting trace. The minimums replace the hidden
   render and land in the same change.
5. *The schedule*, and then one reconciliation of the final render list
   before commands are recorded.

Two things do not wait for the rest. Allocation on every thread is cut at
the sources a trace names. And the withdrawn picture, a defect in today's
code, is fixed first, in [the first wave](#the-first-wave).

A review of this order against the code raised ten findings. The design
answers each. None was reproduced in a running World.

| Finding | What the design does |
|---|---|
| A filming camera's views are added to the render list after the scheduler has copied it, so a window only that camera sees is never drawn | The scheduler takes its list at the point the frame seals, after camera placement (step 3). The window's producer renders on the turn, and the camera reads that picture on its next frame, as a camera always reads the frame before |
| A destination can reveal a sky world after the main capture has fixed the list | Two lists freeze at two points. The set of render graphs freezes at the frame boundary, and a change to it is published whole there. The list of what this frame reads stays open while captures run: each newly reached producer that is read in the same frame is visited in turn until none is new, and a producer read through its previous frame is treated as history and adds nothing. The list then seals |
| A visibility test can prepare the far world while the host is still composing, and so read last frame's clock | Visibility gets a read-only lookup of occlusion that is already complete. Where the answer is not known yet the view is kept, so it is served too much and never missed |
| Changing a viewer's route does not withdraw a picture already published | A defect in today's code, filed in [the security packet](open-items.md#security-review). Disclosure is checked at the two places a picture can reach a viewer: where a picture is bound to a reader, and where a finished job installs its result (decision 14). Both land together. The viewer's old output is gone on the first frame delivered after the change, and a job submitted before it cannot reinstall it |
| An audit that compares hashes cannot certify a skipped redraw, and a check that never skips passes with nothing audited | The audit compares each skip with the full decision, and counts eligible, skipped and audited views ([the first bet](#the-first-bet-stands-on-the-clients-records)). Its test includes a state cell that is still easing on an otherwise quiet delivery, because the mirror refreshes those slots on every advance |
| "A view that covers some of the frame" is too wide a reason to capture, and it misses readers the final schedule adds | Capture follows actual readers, using the scheduler's own rules, and the final render list is reconciled once before recording. Two entrances are closed, not filtered. The refresh of every entry at the end of begin-frame prepares nothing. And finding out which of the boot world's sessions a frame reads uses their cameras alone, so a warm session nothing reads is not filmed at all |
| Upkeep for an unseen world is more than polling: finished jobs owe callbacks, a trim walks mesh pages, and a shader reload needs built pipelines | Completion is served before any packing. Quiet polling and requested work are counted apart. A reload stays pending until there is a safe place to install it |
| A world behind a portal that is skipped for several frames loses the time between captures, so its animation shows an old phase | One long step cannot stand for the frames that were skipped, so each input says how it catches up. An animation keyed on time seeks to the presented time. One driven by distance travelled adds up the movement that was delivered while the view was hidden. Exposure adaptation starts again from the meter's reading when the view returns |
| A count of fallback images can miss a missing picture, and an unchanged capture count can be correct | The check places a changing marker in each destination and reads reader, producer and picture on the delivered frame. Captures, packs and filmed views are counted apart |
| A delayed exposure reading is stored by slot number, so it can land on the next view to take that slot | The reading carries the identity and generation of the view that submitted it, and a reading for a retired generation is discarded |

**Why.** Capture cost today grows with the number of worlds held, not
with what is on screen. The worlds that cost the most for nothing are the
ones no view shows, and those need no answer about freshness at all.

**What it costs or risks.** Upkeep's cost is not constant: a trim or a
reload is real work, which is why it is counted apart. A shortcut that
lets a shown view keep its picture without capturing is a second answer
to a question the hash answers today, and it fails silently. It is built
only if the breakdown calls for it, as the first bet.

**What we still have to find out.** The breakdown of produce, which sets
the order of the smaller cuts: field queries on the CPU (about a third of
the meadow's frame), draw records uploaded again each frame, and motion
interpreted every frame.

**Against the budget.** Produce, from 26 ms toward 9 ms. The world
nobody sees still steps and still delivers. That is the tick's cost and
this decision does not reduce it.

**Evidence.** Checked: the loop order, the prepare of every residency, and
the freshness question that prepares first. Of the ten findings, checked:
the render list copied in `RenderGraphRuntimeNode.ProduceFrame` before
`WorldViewGraphHost.RootFilmedViews` adds to it; the nested sky fit that can
reach a new reader (`WorldScreenBinder.FitNestedCameraSkies`); the mirror
refreshing unsettled slots on every advance (`WorldStateMirror.Advance`);
the refresh of every entry at the end of `SdfWorldPasses.BeginFrame`; the
filming of every registered home session (`WorldScreenBinder.FilmHomeViews`);
the gait integrating distance travelled apart from time (`WorldGaitDrivers`);
the exposure reading published by slot number (`SdfViewExposures.Publish`);
and a private session measuring its own interval
(`WorldSessionFrameSource.CaptureFrame`). Not checked: the other findings'
code paths, and every failing case, which are deductions from reading.

**Owning plans.** [Streaming and memory](streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render)
for the capture order and the minimums,
[the job system plan](engine-job-system.md) for allocation, and
[disclosure](disclosure.md) for the withdrawn picture.

### 3. Shorten the tick, and settle where it runs

**The problem.** A frame that also steps the simulation carries a whole
tick before it starts on the picture: 7 to 10 ms in front of 26 ms at the
bar's size, and in the lift chapter a single step of most of a second.

**What we propose.** Three things, in this order of urgency.

1. *Fix the lift's steps at their cause.* A step that takes 0.7 s is a
   defect whatever thread it runs on. A trace of the headless chapter points
   at contact for rigid bodies and at sending each session a whole frame of
   state when a door swaps. This is a lane of the first wave.
2. *Shorten the ordinary step.* Its work is split into partitions that run
   on one thread pool, and the frame thread waits for them inside the step.
   The order is the job system plan's, and it is a real dependency: first
   bound every wait the console and input can be held behind, then build
   the pool, then split the tick, then fix the tick's serial costs in the
   order its breakdown gives.
3. *Settle where the tick runs.* Whether the tick may run on a thread of
   its own is an open design question, owned by
   [the job system plan](engine-job-system.md#open-questions). Nothing
   rules it out.

**Why the question is open.** Every world instance steps in series on the
frame thread (`WorldInstanceHost.StepInstances`). At the scale Puck is
built for, many worlds are in view at once, and a frame that steps all of
them before it draws cannot hold its bar however short each step is. The
first wave measures what one more world in frame costs the frame thread,
and that number is the question's first input.

The register still rules that a frame of latency is never the fix for a
missed bar ([the pipeline foundation](../decisions/rendering.md#the-pipeline-foundation)),
and Puck's purpose weighs feel as heavily as looks. So the design has to
say what the player's own input costs under each arrangement it weighs:
worlds the player is not acting in stepping on other threads, which adds
nothing to the time from input to display; every world on its own thread
with the frame reading the latest complete state; or the tick staying where
it is. Each is stated with its measured time from input to display beside
today's. An arrangement that adds latency for the world the player acts in
is the owner's call, because it changes a recorded ruling.

**What it costs or risks.** The two planned splits may not be where the
debug world's tick spends its time. One trace shows about a third of it
moving bodies and under a third evaluating rules, each with a serial
cause open items names.

**What we still have to find out.** Where the tick's time goes, by phase
and by world. What one more world costs. The time from input to display
today, which nothing measures.

**Against the budget.** The tick, from 7 to 10 ms toward 4 ms with the
authority in process. With a remote authority the client pays to apply a
delivery instead, which nothing has measured.

**Evidence.** Checked: the loop order, and that instances step in series.
The tick's figures at the bar's size are one quiet run. Not checked: the
tick's shares by phase, which are one trace, and the job system plan's
contract, which is taken from that plan.

**Owning plan.** [The job system plan](engine-job-system.md#6-build-order).

### 4. Run background GPU work without waiting a frame for each job

**The problem.** Background GPU work is everything that is not drawing
the frame: relighting probes, tracing their rays, sampling the field for
the bake, and uploading meshes. It is slow because it waits on round
trips, not because the GPU is busy. The lighting now keeps several jobs
in flight and commits a batch once (`SdfMeshProbeGpuWork.Run`,
`SdfMeshProbeGpuWork.RecordCommit`), but it is still granted work for each
rendered frame, so a game presenting 30 frames a second gets half the
lighting of one presenting 60. A group's bake makes about twenty round trips. A full bake
batch is about 32 ms of GPU work against an allowance of 4.2 ms, so a
bake during play already submits jobs several frames long.

**What we propose.** In this order:

1. *Make the budget follow the GPU time the frame leaves free.* The
   engine already measures the frame's GPU load for dynamic resolution
   (`IWorldFrameLoadSource`). The background share is what the views
   leave of the display's period, with a lower limit so lighting never
   starves. Every job is cut to fit one frame's allowance.
2. *Cut bake jobs to the allowance.*
3. *Let several lighting jobs run at once* and let relights that a
   lighting change made stale share the worker with traces still owed
   (both built; the [convergence checks](../../.claude/skills/rendering/references/mesh-gi.md#lighting-switch-convergence-checks)
   read them), then grant lighting work for each second, not each frame.
4. *Move the lighting's work list onto the GPU*, so the CPU reads only
   counters.
5. *Hand background jobs to the GPU before the frame's own passes are
   recorded*, so an answer arrives a step sooner.

**Why.** The budget rule comes first for a reason that is easy to miss.
At the bar's size the main view alone uses about 25 ms of GPU time, which
is more than the whole period, and the background allowance is another
4.2 ms. The lighting uses little of that allowance today. The first fix
that makes the lighting effective would add all of it to a frame that is
already over.

**What it costs or risks.** An average budget cannot protect one frame: a
long job already queued delays the frame behind it. That is why each job
is cut to a frame's allowance. Two controllers reading one load figure
can fight, so their order is stated: optional background work gives way
before the render size moves. A promise of service to the lighting cannot
hold when the frame's own passes fill the period, so the rule for that
case is written down
([the budget's notes](#the-bars-and-the-budget)): the frame keeps its
time, the lighting's age grows, and both are counted. The work list on the
GPU and the early hand-off sit in two classes of defect that only review
catches.

**What we still have to find out.** How much of its allowance the
background work uses today. How much host time the faster lighting
costs while it works ([open items](open-items.md#lighting-and-gi)).

**Against the budget.** The background row: at least 1.0 ms of GPU time
a frame, taken only from what the views leave.

**Evidence.** Checked: the grant for each frame, the bake's batch size,
and the load source. The main view's 25 ms is one quiet run at the bar's
size.

**Owning plans.** [Illumination](illumination.md) for the lighting, and
the bake's packet in [open items](open-items.md#bake-proportional-to-change)
for the bake's jobs.

### 5. Convert scene light to display values exactly once

**The problem.** Tones are wrong across the whole picture. Scene light is
linear: twice the value is twice the light. A display expects values
encoded on the sRGB curve. The step that turns one into the other does
not exist on any path a world's view takes to the display. An SDR display
shows linear light as if it were encoded, and an HDR display decodes it a
second time. The exposure meter aims a scene's average at about a tenth
of white, and without the encode that shows as about 1% of white. The
debug world compensates by hand with two stops of exposure, which
corrects one point on the curve.

**What we propose.** Shade in linear light everywhere, and convert once,
as the last stage of each view's own graph, where no consumer can bypass
it. Every image declares which of three things it holds: scene light, a
window's partly exposed light, or an image encoded for the display. A
graph that joins two kinds without the conversion does not compose. An
authored colour becomes one of two types, a scene colour or a display
colour, and only a scene colour can enter shading. The debug world and
the standard package are retuned in the same change, because every place
is tuned against the missing encode today.

**Why.** What a pixel value means is correctness, not look. Every later
judgement of exposure, tone, bloom and materials depends on it.

**What it costs or risks.** Every place's look moves at once. A single
full-screen view gains a pass it did not have. A light's range is defined
as a share of display white, so ranges lengthen unless that is restated,
and longer ranges mean more relighting.

**What we still have to find out.** Nothing blocks the fix. The work
builds two instruments first: an authored grey card with luminance
patches, and a readback of the presented image in the display's own
format, since HDR output has never been observed.

**Against the budget.** One more pass for a single view, inside the 1.5 ms
row for the resolve, bloom and encode. The lane reports its cost at 2560
by 1440.

**Evidence.** Checked: the display encode's branches, the placement
pass's comment that no encode follows it, the hex colour's comment, the
debug world's compensation, and the three paths a view takes. Not
checked: the 1% figure is arithmetic.

**Owning plan.** [Rendering](rendering.md), with
[the terrain pass](terrain-pass.md#order-of-work), which already expects
the grey card to read correctly.

### 6. Give every receiver the same shadow answer

**The problem.** The shadow a surface receives depends on which shadow
map happens to answer and on what kind of view asks. Softness jumps about
seven-fold at the boundary between the near and far maps, because the
filter is sized in texels. Past the shadow maps everything reads as
sunlit. Glass and nested views get no shadow maps. And the wrong light
holds the maps: the ranking looks at a light's mode before its
brightness, so by day a moon that emits nothing holds a map, and at night
the unlit key light holds the near and far ones.

**What we propose.**

1. *Give the shadow maps to lights by role.* A light that emits nothing
   is given nothing. The brightest directional light takes the near and
   far maps, and a change of role fades as a change of slot does.
2. *Where no shadow map contains a point, use the lighting probes' record
   of whether that light reaches it.* Two kinds of receiver already get
   this answer: glass, and a view that draws no maps at all. The case that
   is missing is in a view that does draw maps, where a point outside
   every map is returned fully lit. So the existing decision is extended
   to ask whether a map contains the point, and no longer whether the view
   has maps. A band blends from the last map into the probes' answer. A
   probe that has not been measured yet keeps today's answer, which is
   lit, and fades to the measured one. It never answers dark.
3. *Size the shadow filter in metres*, from the light's angular size,
   with wide samples read from a reduced copy of the map. A prototype
   behind a switch is compared with a brute-force filter first.
4. *Centre the maps on the eye and author them as one set of radii*, with
   far entries held between refreshes.

**Why.** Roles give a whole map back by day. The probe answer stops
distant surfaces reading as sunlit. A filter in metres removes the
seven-fold jump.

**What it costs or risks.** Past the far map only the coarsest probes
answer, and they are 32 m apart. The valley's far view, which the art
direction leans on, would be shadowed in 32 m steps until the far shadow
map exists. So the fallback is judged by presented captures from the
valley's rim, and it is kept there only if it reads better than today's
sunlit answer. Nothing outside Puck shows a metre-sized filter over a
depth pyramid in a moving scene, which is why the prototype comes first.

**What we still have to find out.** Which light holds which map at noon,
by measurement. Whether the filter matches the brute-force reference.

**Against the budget.** The shadow row, from 5.0 ms on Direct3D 12 toward
2.5 ms. Roles remove one map's draws by day. The probe lookup adds work
for each pixel past the maps, reported with the change.

**Evidence.** Checked: the cascade test with no blending, the texel
clamp, the fully lit return, the allocator's ordering, the moon's and key
light's authoring, and that glass and a view with no maps already take the
probes' answer (`sdf-glass-stage.hlsli`, `sdf-light.hlsli`). Not checked:
which light holds which map by
day and night is reasoned from that code, and the probe spacing is a
reviewer's report.

**Owning plans.** [Illumination](illumination.md) for roles and the
fallback, and [transmission](transmission.md#implementation-steps) for
the filter, which rewrites the same function.

### 7. Make a lighting change cost what changed

**The problem.** Any change to the scene relights the whole probe volume
from scratch. The stored light is reused only while two numbers for the
whole scene match, and one of them moves whenever a mesh swaps its level
of detail because the camera moved. The sky map is also rendered three
ways, two of them on the CPU.

**What we propose.** A geometry change costs a trace. A change of a
light's direction costs a relight. A change of brightness or colour costs
a multiplication, where the light still reaches the same places.

1. Fix the round trips first (decision 4).
2. Render the sky once, on the GPU.
3. *Store light for each group of sources and combine when publishing.* A
   group is a set of sources that scale together: each shadow-casting
   directional light, the sky, each local light, every emitting material,
   and each screen or opening. A change of brightness or colour is then
   exact, not an estimate.
4. *Light emitting surfaces as areas, not points.* The hall's ceiling
   strips light its floor in blotches. This shares the store with step 3,
   so it is built beside it.
5. While a full relight works through the grid, scale the probes it has
   not reached by the change in the sun's and sky's brightness.
6. Light each surface once and share it between the probes that hit it,
   and store which lights reach each surface as geometry.
7. Trace against a mesh the camera never changes.
8. One lighting budget and one pool of probes across all worlds shown.

**Why.** At fixed geometry the light a surface receives is linear in a
source's brightness, so a change is a multiplication and the stale night
metering goes away.

**What it costs or risks.** Nothing we read stores light for each source
in a dynamic probe volume, so its check is Puck's own: after an edit the
stored light must equal a relight from scratch, in seven named cases. It
costs memory for each group for each probe. A local light's brightness
does not qualify today, because its range follows its brightness.

**What we still have to find out.** The memory cost of light stored for
each group. How many probe hits share a surface. Whether floors are good
enough to trace against.

**Against the budget.** The probe row: one pool of 0.6 GB on the RTX
4070 in place of a volume for every world shown. Storage for each group
is priced against it before it is built, and narrows to the directional
lights and the sky if it does not fit.

**Evidence.** Checked: the reuse condition, how the scheduler's predicted
cost differs from what the kernel tests, the range formula, and the sky's
three renderers. A frame's light table holds at most 128 lights
(`SdfLights.MaxLights`), so research on many lights is owed only when a
place authors more than that. Not checked: the
memory figures are arithmetic, and the nearest shipped relative
(Assassin's Creed Shadows) is a reviewer's reading of the deck.

**Owning plan.** [Illumination](illumination.md).

### 8. Choose detail by one error budget, along one chain of meshes

**The problem.** Two rules decide how detailed a mesh is drawn, and they
disagree. A shape's tier is chosen by half a pixel at the nearest point
that asks. A level is chosen by one pixel of error, measured a different
way. As a result the finest mesh is almost never drawn and still holds
about 1.1 GB. One demanding copy sets the detail of every copy: one fern
beside the player sets the detail of every fern in the world. And nothing
cheap stands in for a distant object.

**What we propose.** One error budget, in pixels for a view and in texels
for a shadow map. One function that projects an error to the screen. One
chain of meshes for each shape, from fine to coarse, in which every mesh
has a recorded deviation from the authored field. Each copy and each
shadow map draws the coarsest mesh in the chain whose recorded deviation
fits its budget. The floor is the first mesh added to the chain.

**Why.** Three constants become one budget. Far copies and far shadow
casters stop drawing the nearest copy's detail. The debug world's
coarsest tier has about a twentieth of its finest tier's triangles. Puck
can check every mesh in the chain against the field, so no stand-in mesh
is authored by hand.

**What it costs or risks.** Measuring every mesh costs bake time, and what
it saves is GPU time in every frame. The two are in different units, so
the lane reports a break-even: the seconds measuring adds to a cold bake,
against the GPU time the chain saves in one loop of the route, as the
minutes of play after which the saving has repaid the bake. The lead
decides with that number, and the chain stops at the floor if it is
refused. The measure samples, so a feature thinner than its
sampling can be missed. A shape with such features is recorded as not
qualified and keeps its finer mesh. The measure must run over the
authored shape's bounds: one that starts from the mesh cannot see a part
that vanished.

**What we still have to find out.** How many triangles each level and
tier draws, which no counter shows. Whether a change of tier for one copy
shows as popping on thin parts.

**Against the budget.** The mesh rows: 1.2 ms of drawing and 3 GB of
meshes on the RTX 4070. The chain is how the RTX 2060's 1.5 GB is meant
to be reached.

**Evidence.** Checked: the three constants, the two projection formulas,
the prototype baked at the finest demand of any copy, and the existing
measure against the field. Not checked: the 1.1 GB is arithmetic, and the
twentieth is one reading.

**Owning plans.** [Virtual geometry](virtual-geometry.md) and
[bake detail bounded by demand](bake-demand-tiers.md).

### 9. Give memory back, and never show a hole

**The problem.** GPU memory for meshes grows toward about 4 GiB and comes
back only when there is room to spare. The renderer can shrink today: a
trim forgets the meshes no current draw names
(`SdfMeshRegionArena.TrimUnused`), and the region is replaced by a smaller
one when it holds more than half as much again as it needs
(`SdfWorldTables.FitMeshRegion`). But the replacement needs room for both
buffers at once, so under pressure, which is when memory is wanted back, it
is refused. A mesh a draw still names is never given up, however much more
detail it holds than the view needs. And a mesh that is not in memory
leaves a hole in the picture.

**What we propose.** Follow the streaming plan in its own order: the
floor, then eviction, then keeping what was observed in use, on a memory
backing that can return memory to the system. The new work is what today's
shrink cannot do: floors that are pinned, eviction of a mesh that is still
named but holds more detail than anything asks for, and giving memory back
while the pool is full. This round adds four
things. Decision 8's counters come first, because they size a floor.
Requests for detail run ahead along the path the camera can take. What
shows before a floor exists is stated: until a new place's floors and
lighting stand, the frame keeps the last complete scene or the window's
authored fallback, and never publishes a scene with holes. And eviction
is judged in physical bytes, because deleting a mesh's rows frees nothing
while a reader still holds its block.

**Why.** The owner has ruled the outcome: a shape that has not landed or
has been evicted "shows its coarsest level ... never a hole".

**What it costs or risks.** A floor can contour a thin wall away to
nothing. An implementation that never frees anything could pass a check
by always naming something in the way, so the check requires one observed
release with a matching fall in bytes held. Today's shrink already gives
such a release when the pool has room, so the release is read with the
pool full, after one crowded walk has recorded what today's code retains,
its temporary peak and when its last reader retires.

**What we still have to find out.** Decision 8's counts.

**Against the budget.** The mesh store: from about 3 GB and growing to 3
GB held on the RTX 4070, 1.5 GB on the RTX 2060 and 2 GB on the Steam
Deck. Without eviction no device's memory bar can be met.

**Evidence.** Checked: the trim, the shrink and its refusal when the pool
has no room for the replacement, and the owner's ruling in the residency
register. Not checked: the 4 GiB limit and the cost of relieving pressure
come from the research pass's map of the streaming area.

**Owning plan.** [Streaming and memory](streaming-and-memory.md).

### 10. Make a bake cost what changed, and close the contour defects

**The problem.** Three things. A change to the bake's own code makes
every stored mesh a miss, because one identity covers the whole store, so
a developer who edits the simplifier waits a minute. The whole bake is
just over its figure of about a minute, and uses more than 10 GB of
private memory. And some shapes bake wrongly: six of fourteen blend
exhibits fail the mesh audit, and a grass blade bakes to about 474
triangles where its prism has a dozen.

**What we propose.**

1. *Count first whether each failing cell was ever flagged for finer
   sampling.* The bake already samples a flagged cell again. If the
   failing cells were never flagged, the defect is a trigger that never
   fires and the fix is small. This starts now.
2. *Key each stage's product by what that stage read.* The bake samples
   the field on the GPU, contours the samples, and then simplifies and
   stores. Keyed apart, an edit to the simplifier starts from stored
   contours and samples nothing again. These are local cache keys and
   stay computed from the code.
3. *A contour rule for a cell that cannot resolve its faces*, inside the
   existing refinement, not as a second path.
4. *Remove triangles without changing the surface:* drop collinear
   outline vertices, and convert a part exactly where its program proves
   a closed form.
5. *Then whichever speed fix a measured bake ranks first:* the GPU
   sampling in one round trip for each stage, or the simplify-and-store
   stage on the thread pool.
6. *One store that both backends read.* A product made by the other
   backend is accepted by checking it against the field, never by equal
   bytes. This is the first use of
   [the shared product's name](#how-a-shared-product-is-named-and-trusted),
   which the owner confirmed.

**Why.** The wait between a change and seeing it costs the most, because
it breaks a developer's flow. Keying by stage attacks that wait with the
principle the store already rests on.

**What it costs or risks.** Stored contours cost disk and memory. A key
that is too narrow is the dangerous direction: an edit that should have
invalidated a mesh and did not shows as a stale mesh nothing reports. So
each stage's key includes everything that stage's code reaches. A contour
fix reaches every bake, so it is judged by an audit of every stored mesh
in the world, not by fourteen exhibits. That audit cannot judge every mesh
yet. The CPU evaluator leaves out glyphs and sampled regions
(`SdfFieldEvaluator`), and the measure against the field stops at its
sampling (`SdfMeshFieldLevels`). So the audit counts the groups it cannot
judge and the groups it did not sample apart from the ones that passed, and
nobody reports that every mesh passed until those have a reference.

**What we still have to find out.** What limits the cold bake. How large
each stage's product is. What a cold bake costs on a weak device. Every
device keeps a local floor bake, so if a bake does not fit the Steam
Deck's memory the floor bake is cut until it does, and
[the fourth and fifth bets](#the-bets) move up to shorten the wait.

**Against the budget.** The bake's memory row, and the cold bake's figure
of about a minute, read again after each change to the bake so it cannot
drift.

**Evidence.** Checked: what the store's identity hashes, that the code
identity keeps its parts apart, and that the store names the part that
changed on a miss. Not checked: the 10 GB comes from the research pass's
map, the stage shares are single readings, and the triangle counts are
arithmetic.

**Owning plans.** The bake's packets in open items
([bake proportional to change](open-items.md#bake-proportional-to-change),
[bake detail](open-items.md#bake-detail)), and
[visibility](visibility.md) for the removed triangles.

### 11. Keep one instance table, and test it once for every pass

**The problem.** The cost of submitting draws follows how many objects
exist, not how many a pass shows. Every candidate costs a draw command,
drawn or not. Only the far shadow map is culled by which surfaces will
read it. At the lobby the shadow maps take 3.9 ms against 1.7 ms for the
main mesh pass. A skinned draw has no bounding box, so it is never culled
and always draws its full mesh.

**What we propose.** One table of instances with stable identities, one
kernel that classifies it for every pass, and a command list equal to
what survives. In the visibility plan's order:

1. Far placements update less often, and only instances that changed are
   uploaded.
2. The command list is compacted, in order, to the draws that survive.
3. Shadow casters are culled, in every shadow map, by which surfaces will
   read them. The mask must cover every texel a reader could touch for as
   long as the map is shown, or else that map culls nothing.
4. Bounding boxes for skinned draws.
5. Commands grouped by geometry and level. The table is designed from the
   start to take rows from several worlds, so that merged worlds need no
   pass of their own ([the third bet](#the-bets)).
6. Indexed draws, as an experiment measured pass by pass.

**Why.** At one stop, culling the far map's casters by reader took a full
refresh from 23,125 draws to 4,138 and from 8.5 ms to 2.9 ms. The near
map is culled by box only.

**What it costs or risks.** A mask that misses a reader removes a shadow.
A bounding box that is not conservative hides a limb. A mask step can
look successful and save nothing while every empty command is still
walked, so the mask is judged by the shadow that results and the command
count is judged at the compaction step.

**What we still have to find out.** What an empty command costs. What
uploads about 2,600 draw records again each frame when nothing moves,
which may be a defect cheaper to fix than any step here.

**Against the budget.** The shadow row and the mesh row. This is the
largest planned cut to GPU time, and it is a cut by a fraction: it does
not by itself bring a device with half the throughput to its bar.

**Evidence.** Checked: one command for each candidate, the far map's
mask, and the missing box for skinned draws. Not checked: the timings
are single readings at one stop.

**Owning plans.** [Visibility](visibility.md) and
[virtual geometry](virtual-geometry.md).

### 12. Keep motion and placement as authored programs at zone scale

**The problem.** A zone of an MMO holds orders of magnitude more plants,
moving parts and terrain than the debug world's meadow. An authored swing
is interpreted from the document every frame. A gust's phase is a literal
number for each blade, so every tile is its own prototype and tiles
cannot share a bake. A scatter is evaluated for the whole world at once,
under a ceiling of 32,768 colliders. Terrain bakes at 2.8 m cells with
relief capped at 4 m, and nothing casts a shadow past 120 m.

**What we propose.** The engine speeds up the authored program and
derives its bounds. It writes no grass, wind or terrain path in C#.

1. *One compiled form for a motion program*, from which come a CPU
   evaluator, a GPU kernel, and the routine that derives the motion's
   bounds.
2. *Placement evaluated cell by cell, by one function, for collision and
   drawing alike.* The cell belongs to the placement, not to the
   renderer, because the server has no renderer and must collide against
   the same population that is drawn. Each placed thing gets an identity
   from what its placement means, so two ways of writing one scatter give
   the same identities. What play changes (a plant harvested) is
   simulation state keyed by that identity.
3. *Terrain as tiles of one relief series, with a far shadow map*, after
   a terrain research round.

**Why.** "Puck code over engine code" keeps behaviour in authored
programs, and "Ergonomic is optimal" means a scatter costs what it means,
not how it was written.

**What it costs or risks.** Almost no terrain source was read. The
terrain decides whether the valley reads as a valley, so its research
round starts now. A plant that was
harvested must stay harvested when its cell is evicted and derived again,
which is why play's changes live in the simulation.

**What we still have to find out.** How closely CPU and GPU motion
agree. Everything about terrain seams and scale.

**Against the budget.** Produce and the tick at zone scale. Nothing here
is claimed against today's debug world.

**Evidence.** Checked: the compiled placement list that drawing and
collision both read, the collider ceiling, and that the server does not
reference the renderer. Not checked: the facts about swings, gusts and
terrain come from the research pass's map of the foliage area.

**Owning plans.** [Dense landscapes](dense-landscapes.md),
[the terrain pass](terrain-pass.md), and [visibility](visibility.md) for
motion on the GPU.

### 13. Shade through one path, and shrink the image history

**The problem.** Surface shading exists twice: authored shading programs,
which are the intended path, and older fixed layers, which the default
frame still uses. Shading is the largest pass: 10.5 ms on Direct3D 12 and
13 ms on Vulkan at the bar's size. And the history that antialiasing keeps
is two images. The surface history holds 47 words, 188 bytes, for every
pixel (`SdfWorldPackage.HistorySurfaceWords`), and the colour history
holds 8 bytes. Each is kept in two copies, the one a frame reads and the
one it writes, which is 392 bytes a pixel.

**What we propose.**

1. *Split the history.* Forty of the surface history's forty-seven words
   are a window of the last eight frames. That window moves to one buffer
   updated in place where a pixel did not move, and only the seven words
   that follow motion stay in two copies, with the colour. That is 160
   bytes once and 36 bytes twice: 232 bytes a pixel. A pixel that moved
   restarts its window; it keeps no second store, so the figure holds in
   motion as well as at rest.
2. *One shading path*, after decision 5 has fixed the colour conversion.
   The ways of running a shading program are measured against the fixed
   layers on the same device, and the fixed layers are then deleted.
3. Measure the per-pixel probe lookup on its own, which is about a third
   of the shading pass, then evaluate it once for each view at its own
   rate.

**Why.** The owner's ruling on the bar's size makes the history the
largest single store after the meshes: about 1.4 GB for the main view at
2560 by 1440. The split brings it to about 0.9 GB. It also stops the
resolve copying the stored window every frame, and the resolve takes 3 ms
at the bar's size. One shading path removes a second implementation.

**What it costs or risks.** A buffer updated in place cannot serve a
pixel whose history comes from somewhere else, because another pixel may
be writing there in the same pass. So the window is carried in place only
where the two positions are shown to be equal, and a pixel that moved
starts a new window. If presented frames in motion show that restarting
costs quality, the alternative is a second copy of the window for moved
pixels, which gives the memory back. The lane then keeps only what the
split saves in the resolve's time, and says so. No source covers an
in-place history, so its design is reviewed as it is built.

**What we still have to find out.** How often a pixel's history comes
from its own position on the moving route. What a player sees when a
shading program arrives that has no compiled kernel yet.

**Against the budget.** The history row, from about 1.4 GB to 0.9 GB for
the main view on the RTX 4070. The resolve's row, from 3 ms toward 1.5 ms.
The shading row, from 10.5 and 13 ms toward 5 ms. A nested view that keeps
a history pays the same bytes for each of its own pixels, and the lane
reports how many do.

**Evidence.** Checked: the switch's default, the two history images and
their words for each pixel, and that a history only compute passes touch
keeps two copies (`ShaderPipelineRenderNode`). The pass times are one
quiet run at the bar's size. Not checked: that the probe lookup is a third
of the shading pass, which is one reading at a smaller window.

**Owning plan.** [Author shaders](author-shaders.md).

### 14. Treat a nested view as the same view at another size

**The problem.** A view through a window or on a screen costs more than
it should and looks different from the same scene seen directly. Two
identical engine constants turn shadows and ambient occlusion off for
every nested view at any size, so a portal that fills the screen is lit
differently from the room behind it. A sliver of window can cost a full
image. One pane of glass costs a clear of the whole image.

**What we propose.**

1. *Replace the two constants with an authored table.* The engine's
   default restricts nothing. The standard package authors what a nested
   view gives up at each size on screen, for each device. This starts
   now.
2. *A view that nothing reads costs nothing.* A world's demand is its
   consumers' minimums first and its pixels on screen second (with
   decision 2).
3. *Fit each window's image to the part of its glass that shows.*
4. *Make glass cost what the visible glass covers.*
5. *A result reused between viewers is keyed by the content of what each
   was delivered*, and checked again when a picture is bound to a reader
   and when a finished job installs its result. Two viewers delivered the
   same rows share. A viewer delivered less does not. Those two checks are
   built first, with the withdrawn picture of decision 2.

**Why.** The table removes a visible pop in lighting at every portal
crossing, and a look built into the engine twice.

**What it costs or risks.** Restoring shadows to nested views adds GPU
work on every device, which is why what a view gives up is authored for
each device. Crossing a threshold in the table rebuilds the view's graph,
which can be refused when memory is full.

**What we still have to find out.** Why a still camera feed keeps
rendering today. The cause of an unexplained device loss on Vulkan near
the glass passes.

**Against the budget.** The nested views' row and the composite's row,
1.5 ms each.

**Evidence.** Checked: the two constants, the window's sizing rule, and
the glass clear. Not checked: that glass shades the whole grid comes from
the research pass's map.

**Owning plans.** [Streaming and memory](streaming-and-memory.md) for
demand, [transmission](transmission.md) for glass, and
[water](water.md) for a camera inside water.

### 15. Present frames the display has room for, and know when they showed

**The problem.** The engine cannot say when a frame reached the display.
On Vulkan a frame can be rendered and then thrown away, because the
presenter asks for room after the frame is built, with no wait. An
emulated machine's video is shown at the simulation's rate and not the
machine's. The interface overlay costs pixels times records.

**What we propose.**

1. *One record for every attempt to present:* whether it was submitted,
   whether it was shown, and the time from input to display.
2. *One admission rule on both backends:* the host gets a place in the
   present queue before it renders, and renders no frame that has none.
3. Machine frames shown at the machine's rate, through a short queue of
   complete frames.
4. The overlay as clipped geometry with text from an atlas.
5. Display timing on Linux, when a Steam Deck is on hand.

**Why.** An even loop interval can hide judder on the display. The frame
rate players see is unexplained today.

**What it costs or risks.** A true display time on Vulkan needs an
extension a device may not have. Its absence is reported as a capability.
A device with no display time can only be judged on the frames the host
produced, which does not show the bar was met on screen.

**What we still have to find out.** Which timing extensions each device's
driver offers. Whether machine frames are dropped on a running World.

**Against the budget.** The overlay's row: 2.8 ms on Direct3D 12 and
2.4 ms on Vulkan with the benchmark's panel up, toward 0.5 ms. The record
is what reads the whole-frame row honestly.

**Evidence.** Checked: the zero timeout on acquiring an image, the
overlay shader's first line, and the video source's declared rate. Not
checked: that machine frames are dropped is read from code and not seen
on a running World.

**Owning plans.** [The benchmark](benchmark.md) for the record,
[the UI overhaul](ui.md) for the overlay, and
[machines and cartridges](machines-and-cartridges.md) for the machine's
rate.

## The build order

The work is not staged. It runs as lanes side by side, and an item waits
only for something it truly needs: a landing that changes the same files, a
mechanism it builds on, or hardware. This section gives the rules every lane
runs under, the engine gaps, the lanes that start now, the lanes that follow
with what unlocks each, and the work after that.

### How the work is run

**What finishes a lane.** A lane builds the projects it touched and runs
what it changed in the debug world. Its evidence is what a player would
meet and what the engine counts:

- a boot of the debug world on the lane's build, and the check the lane's
  entry names;
- for anything a player sees, presented frames while moving, at the place
  the entry names, on Direct3D 12 and on a windowed Vulkan boot;
- counters and counted work: draws, dispatches, bytes held, captures,
  refusals;
- for anything that claims frame time, one report of
  `puck benchmark --route debug --width 2560 --height 1440` on the lane's
  build beside one on the build it started from.

A lane lands when that evidence holds. Test requirements are lifted for the
refactoring campaign ([AGENTS.md](../../AGENTS.md#puck-is-pre-release)): no
suite, coverage figure or recorded baseline is required of a change, and a
test a change breaks is updated or deleted in that change. Equality with the
previous build's output is never a finish line.

**Measurement runs beside the work.** A lane without a GPU grant hands its
windowed check to the lead as a verification request, and the lead runs
those in batches. A question about time goes to the lead's quiet window
(`puck measure request`) with the two builds frozen, and the lane lands on
its counts meanwhile. No lane waits for a measurement, for a review, or for
the RTX 2060, the Steam Deck or an HDR display.

**Review is for what running cannot show.** A change gets an adversarial
review only when it sits in a class that
[hazards](../development/hazards.md#silent-invariants) marks review-only.
The review starts when the lane hands back and never holds its landing. In
this plan those are: the withdrawn picture and every reuse of a result
between viewers (a trust boundary); the split image history, the GPU work
list and the early hand-off of background jobs (GPU lifetimes, and a GPU
read at a position another job wrote); the relief mask's instruction, which
changes the field's encoder and decoder together; any proof that lets an
exact evaluator skip work; and the first bet's shortcut. Everything else is
checked by running it and by the area fix passes.

**Four rules against stalling.**

1. *A bet is time-boxed and ended by its number.* When the box runs out or
   the number comes in on the wrong side, the lane stops and reports
   ([the bets](#the-bets)).
2. *A look gets at most two rounds with the look critic* for each creation
   or place in a wave. A third miss changes the approach or goes to the
   owner. A creation that still misses the bar may land when it is a
   material improvement on what the tree holds, with its failures and the
   gaps behind them stated in the commit.
3. *A named engine gap is fixed, not sculpted around.* A look lane that
   meets a listed gap stops and hands the place to the gap's lane. A lane
   that finds a new gap adds its row here, and the gap goes to the front of
   its owning plan's order.
4. *A lane that stops converging changes approach.* Two attempts at one fix
   on the same footing is the limit. The lead then changes the approach,
   the model or the work, and a note in the hand-back says what was tried.

**How the frame bar is read.** The slowest frame's work on the benchmark's
route, at the device's authored quality table and the size the owner ruled
for it, with the render scale native and dynamic resolution off, on
Direct3D 12 and on Vulkan where the platform has both (Direct3D 12 needs
Windows), with the authority in process and with it remote. Where the
device reports display outcomes, no present is skipped or dropped. Meeting
the bar on the RTX 4070 also means every chapter still meets its reference
in motion in both topologies: a frame that holds its time by dropping
quality has not met it.

### Engine gaps, filled first

An engine gap is a limit in the engine that stops a place or a creation
reaching the look it is meant to have. The owner has ruled that a gap a
lane, a critic or a review names "is filled as soon as possible": it goes
to the front of its owning plan's order, ahead of further work that would
sculpt or tune around it, and is never only filed
([AGENTS.md](../../AGENTS.md#pucks-purpose)). So each gap below starts as
soon as it can, and a look lane that meets a listed gap stops and waits
for it.

A gap's number is its name, which other plans cite. The rows are in rank
order, by how many places and exhibits each one blocks. The counts come
from the visual-bar, water and terrain packets in open items, the modeling
plan's list of limits, and the look critic's report on the rebuilt lake and
glacier.

| Gap | What it blocks | Its first step, and when it starts | Resumes when it lands | Owning plan |
|---|---|---|---|---|
| 1. Large terrain bakes coarser than it declares | All eight wall sectors of the valley, the glacier's trough and moraines, and any stone placed in a wall | Report one sector's bound, the spacing it asked for and the cell it got, then fix what sets the cell. Starts now: first wave, `terrain` | The valley's walls and the glacier's trough | [The terrain pass](terrain-pass.md#order-of-work), step 4 |
| 2. Terrain has no authored treatment by slope, altitude and band | The same eight sectors and the rim: one olive colour, smooth shading over flat colour | The terrain template with its masks. Second wave: it waits for the colour conversion | The north-west wall against the direction's layered strata | [The terrain pass](terrain-pass.md#slope-altitude-and-curvature-materials), step 6, with [author shaders](author-shaders.md) |
| 3. A distant surface has no useful shadow and no stand-in mesh | Every view of the valley from a distance: the rim, the far walls, the ridges | Two steps. The floor, a coarse mesh kept resident on its own, starts now: first wave, `floor`. The far shadow map is second wave: it waits for shadow maps given by role | `valley-rim`: far walls with ridge shadows, and no hole on arrival | [Streaming and memory](streaming-and-memory.md#step-5-the-floor-and-eviction) for the floor, [the terrain pass](terrain-pass.md#order-of-work), step 7, for the far shadow map |
| 4. Relief noise cannot be masked or flattened | Anything set into terrain: the lake's bowl, the pool, the river's channel, the glacier's cradle. The noise moves the authored ground by metres and buries them | The mask's step A. Starts now: first wave, `terrain` | The lake's bowl in the north-west wall | [The terrain pass](terrain-pass.md#the-relief-mask), steps A to C |
| 5. Water does not read as shallow or deep over its bed | The lake, the pool, the river and the plunge: one flat colour, no clear shallows, stones invisible | View one known sloping bed from overhead and from a low angle, and record its geometry, the water's thickness along the ray and what the material returned. Starts now: first wave, `water` | The lake over its bowl and stones | [Water](water.md#order) |
| 7. Thin and creased features bake wrongly | Six of the fourteen blend exhibits, the Moth's shin rims and the swallow's streamers | For each failing shape, name which of four defects it is and whether its cell was ever flagged for finer sampling. Starts now: first wave, `contour` | The blend exhibits' mesh audit | [Bake detail](open-items.md#bake-detail) |
| 13. Haze is tied to how far the world draws | Every distant view: rock used to judge materials and shadows changes when the draw distance does | Haze as a range the author sets. Second wave: it waits for the colour conversion, which changes the same colours | `valley-draw-distance`: rock 700 m out | [The terrain pass](terrain-pass.md#distance), step 2 |
| 8. A cutter that moves against the surface it cuts keeps its rest cut, and a creation with one takes no relief | The glacier, which wastes back only by scaling about its head; a door carved into rock; erosion | Step G1. Starts now: first wave, `cutter` | The glacier retreating by a cut up its trough | [Moving blends](moving-blends.md#gap-8-a-cutter-that-moves-against-the-surface-it-cuts) |
| 6. A flow layer carries one velocity | The lake's outlet sill, the river's winding course and the falls | A velocity that varies over the surface. Starts now: first wave, `water` | The lake's sill, where the water should speed up and nowhere else | [Water](water.md#order) |
| 9. A camera inside water sees a flat wash | The four water places, whenever the player swims | A case for an eye inside a medium. Starts now: first wave, `water` | A dive in the pool | [Water](water.md#order) |
| 15. The falls have no spray or splash | The falls and the plunge pocket | The particle pool, which is designed. Second wave: it waits for `water`, which changes the pass it draws through | The falls from below | [Water](water.md#w3-the-presentation-particle-pool) |
| 10. A paint does not follow a blended seam between parts that move | The Moth and the swallow | The rest-point mapping for paints. Second wave: it waits for `water` and `shadows`, which change the same shader | A painted marking on a rigged head | [Moving blends](moving-blends.md), its step 4 |
| 11. A stretched capsule, cone or torus gives a bound, not a distance | A stretched cutter: it frays the surface it cuts | An exhibit and a failing measurement for each of two effects, then the solver's design. Starts now, as read-only work beside the first wave | A stretched cutter in a smooth blend | [Rendering](rendering.md#bake-quality) |
| 14. A field row that only the simulation needs is drawn | No place: it costs two baked height fields nobody sees | A way to mark a row as simulation only. Starts now: first wave, `water` | The hidden chain under the valley | [Water](water.md#the-gap-list), its S5 |
| 12. A frame's light table holds at most 128 lights | No place today | Research on many lights, when a place authors more than the table holds | None yet | [Illumination](illumination.md) |

Three more gaps block every place, and are lanes of the first wave: the
missing colour conversion, the shadow maps held by an unlit light, and
nested views with no shadows.

What the code shows for the rows a review corrected:

- **Gap 1.** A creation whose parts carve each other, or that carries
  relief, is emitted as one instance whose bound is a sphere about the
  creation's origin (`WorldPlacementStamper`). A member's declared detail
  refines a coarse cell at most eight-fold (`SdfMeshPathBake`). Either can
  set a sector's cell, and one combined field can still be bounded tightly,
  so the lane reports which before it changes anything. Relief is capped at
  4 in the creation's own units, which scale with the stamp
  (`CreationNoiseDocument.MaxAmplitude`), and a smooth blend at 0.5
  (`ShapeDocument.MaxSmooth`). The 2.8 m and 1.56 m cells are the terrain
  plan's reading and were not reproduced.
- **Gap 2.** The fixed weathering already settles a deposit on surfaces
  that face up and wears edges by curvature (`shade-weathering.hlsli`).
  What is missing is a treatment an author composes: bands by altitude,
  thresholds by slope, breakup. It reuses those inputs. The default frame
  also leaves authored shading programs behind a switch
  (`WorldRenderSettings.ShadePrograms`).
- **Gap 3.** The shadow's reach is authored (`render.shadowCoverage`,
  resolved by `WorldRenderFarDistance.ResolveShadowCoverage`), so 120 m is
  the standard package's value and not an engine limit. Coarse tiers also
  exist: every group clamps its tier between a coarsest and a finest
  (`SdfMeshBakeGroup`). The two missing guarantees are a shadow far away
  that does not cost precision near the eye, and a coarse mesh that stays
  resident while the fine one arrives or after it is evicted.
- **Gap 5.** The glass shader already thickens water's haze with the length
  of the ray through it (`sdf-glass-stage.hlsli`). That formula alone does
  not explain why stones on the bed disappear. A depth over the bed is a
  likely missing input to the material, and the first step is the
  comparison that shows it.
- **Gap 7** is four defects with four fixes, and a fifth thing that is not
  a defect. A vertex its crossings put off the surface at a crease. A
  sliver between two rows of vertices along a crease. A face narrower than
  the cell between two creases, as on the Moth's shin rims. A tip thinner
  than about one and a half cells that stops short, as on the swallow's
  streamers. The grass blade's 474 triangles where its prism has a dozen is
  the fifth: a complete shape with too many triangles, which decision 10's
  triangle removal answers. Blocked exhibits are counted again once each
  failing shape has its defect named.
- **Gap 8.** The glacier's own relief works today, because it scales about
  its root and nothing in it moves apart (`glacierMass`). The limit is a
  cutter that moves relative to what it cuts: the cut stays where it was at
  rest (`CreationMotionPlan.Borrows`), and relief refuses any creation with
  an independently moving part.
- **Gap 11.** A stretched ellipsoid or sphere and a regular polygon already
  have exact forms. What still rides a bound is a capsule, cone or torus
  stretched apart in two axes. A bound is still right at the surface, so
  two effects are measured apart: the distances a smooth blend reads away
  from the surface, and defects in the cut's contour.
- **Gap 13.** Haze's strength is packed as an amount divided by the far
  distance (`SdfSky.PackAtmosphere`), so changing how far the world draws
  changes the haze on everything nearer.
- **Gap 14.** A field row is drawn when its height scale is above zero
  (`WorldFieldEmitter`), and the flow levels water against a bed only for
  rows that carry one (`FieldLattice`). A row the simulation needs is
  therefore drawn whether or not anyone can see it.

Checked for this table: each symbol above, the flow layer's one velocity
(`PaletteFlowDocument`), the eye inside a glass solid pairing with nothing
(`sdf-glass.hlsli`), and the light count (`SdfLights.MaxLights`). Gap 8's
cause was read in the code and reproduced at compile time by its design
([moving blends](moving-blends.md#what-the-gap-is-in-the-code)). Not
checked: the thin-tip limit and the unblended paint, which are the modeling
plan's. No other gap was reproduced for this page.

### Tracks

A track is a chain in which order is real. Tracks run beside each other, and
inside a track an item waits only for the one before it.

| Track | The chain |
|---|---|
| Frame thread | The lift's steps and the breakdown of produce. Then the capture order (decision 2), after the withdrawn picture, which changes the same binding code. Beside it the job system's own order: bounded waits, the thread pool, the tick's partitions |
| Shading | The shading pass at the bar's size. The colour conversion, then terrain programs, then one shading path |
| Image history | The split history, then the smaller per-pixel records |
| Shadows | Maps by role with the cascade set's shape, then the far shadow map, then the filter sized in metres, then casters culled by reader in every map |
| Indirect light | The allowance that follows free GPU time, then the grant for each second, then the work list on the GPU. One sky, then light stored for each group of sources with area emitters, then shared surface lighting, then one pool across worlds |
| Bake | The sector's bound and the relief mask, then the relief operation and its caps. The moving cutter's steps G1 to G4. The contour count, then the contour rule. Keys by stage, then the first speed fix |
| Memory | The cap as an engine input. The floor, then eviction, then the backing that returns memory, then keeping what was observed in use. The chain of meshes after its counters |
| Draw submission | Uploads only for instances that changed, then the compacted command list, then commands grouped by geometry and level, then indexed draws as an experiment |
| Water | Depth over the bed, the eye in water, flow that varies and simulation-only rows. Then the falls, the pool and the reservoir's looks, then the particle pool |
| Worlds and disclosure | The withdrawn picture, then results reused between viewers by what each was delivered, then the bets on merged worlds, shared products, light through openings and the visible part of a world |
| Measurement and presentation | The benchmark at the bar's size with the device's table, then the remote route driver, then a record for every present, then the present queue's admission rule |

### Lanes in flight

Two lanes are already dispatched. No lane below repeats them, and each
keeps out of their files.

| Lane | What it is doing | Keep out of |
|---|---|---|
| `vkalloc` | Vulkan's allocation in each frame: about 41 MB an iteration against Direct3D 12's 5 MB | The Vulkan backend's per-frame paths (`src/Puck.Vulkan`, `src/Puck.Vulkan.Presentation`) |
| `silo10` | Ten failing tests of the silo's release cutover and recovery | The silo's tests and the code they reach |

### The first wave

Twelve lanes start now and run at once. None waits for another to begin.
Each owns the files its entry names. Where two lanes meet in a file, the
merge order is given after the entries, and the later lane merges the
integration tip before it hands back.

The model class follows what the work risks: Opus (`specialist`) where an
invariant carries the change, Sonnet (`implementer`) where the result is
specified, and Codex where a fresh read of code another family wrote is the
point. The lead may give a Codex slot a `specialist` brief when one is free.

| Lane | What it delivers | Serves | Model |
|---|---|---|---|
| `views` | The shading pass at its share | The largest GPU pass | Opus |
| `frame` | No slow step in the lift, and a breakdown of the frame thread | The slowest frames, and the 26 ms nobody has split | Codex |
| `history` | A full pool never aborts a frame, and a smaller image history | Decision 13 | Opus |
| `bench` | The memory cap as an engine input, quality by device, and a route that runs against a remote authority | Decision 1 | Sonnet |
| `colour` | Scene light converted once, and every place retuned | Decision 5 | Opus |
| `shadows` | One shadow answer for every receiver | Decisions 6 and 14 | Opus |
| `withdrawn` | No picture a viewer is no longer allowed | A defect in today's code | Opus |
| `terrain` | A sector at its declared spacing, and a relief that can be masked | Gaps 1 and 4 | Opus |
| `cutter` | A cutter that moves re-carves what it cuts | Gap 8 | Opus |
| `floor` | A shape in reach always has a mesh | Gap 3 | Opus |
| `water` | Depth over the bed, the eye in water, flow that varies | Gaps 5, 6, 9 and 14 | Codex |
| `contour` | Each failing bake named, and the first defect fixed | Gap 7 | Sonnet |

**`views`: the shading pass at its share.**

- *Outcome.* The pass that shades each pixel takes 10.5 ms on Direct3D 12
  and 13 ms on Vulkan, against a share of 5 ms. The lane first splits that
  time by compiling parts out: the probe lookup, the lights, the materials.
  It then cuts the largest part. The designed cut is the probe lookup
  evaluated once for each view at its own rate (decision 13).
- *Owns.* `src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-world-views*.hlsl`,
  `sdf-views-stage.hlsli`, `sdf-views-filter.hlsli`, the probe gather they
  include, and the views pass's declaration in
  `src/Puck.Shaders.Model/Graph/SdfWorldPackage.cs`.
- *Needs.* A GPU grant: measuring the pass is the work.
- *Model.* Opus `specialist`.
- *Finish line.* The split is in the hand-back with the command that read
  it. `sdf.world$views` falls on both backends in the benchmark report at
  2560 by 1440. Presented frames on the route show no stair-stepping at
  visibility edges, and a lit pane over the sky and one over a dark wall
  are each lit correctly.

**`frame`: the lift's steps, and a breakdown of the frame thread.**

- *Outcome.* The lift chapter has no step longer than the route's other
  chapters have. Then produce has a breakdown by part, with the time spent
  waiting for the GPU kept apart from work. The tick has one by phase and
  by world. The report gives what one more world and one more view in
  frame cost.
- *Owns.* `src/Puck.Launcher/HostFrameTimeline.cs` and the host loop's
  phases, `src/Puck.World.Server/WorldSessionSink.cs`,
  `src/Puck.World.Schema/WorldDeliveryCursor.cs`, the rigid contact that is
  installed when a door swaps, and the frame thread's counters.
- *Needs.* Nothing.
- *Model.* Codex, as a fresh read of the delivery and contact code.
- *Finish line.* `puck benchmark --headless --route debug --chapter "the
  lift"` shows no step in the hundreds of milliseconds, and the windowed
  route agrees. `host.frames` prints produce by part, and the parts sum to
  the produce it reports. A scratch world with one more instance stepped,
  and one with one more nested view, each give a difference.

**`history`: a full pool never aborts a frame, and a smaller image
history.**

- *Outcome.* Two things in one area of the shader pipeline's memory
  accounting. First, growing the per-pass work counters no longer throws
  when the pool is full (`PrepareWorkDetails` in
  `ShaderPipelineRenderNode.WorkDetails.cs`). The frame keeps the rows it
  has, counts the refusal by name, and grows them when there is room.
  Second, the image history is split (decision 13), which takes the main
  view's history from about 1.4 GB to about 0.9 GB and stops the resolve
  copying the stored window every frame.
- *Owns.* `src/Puck.Shaders/ShaderPipelineRenderNode.WorkDetails.cs`,
  `src/Puck.Shaders.Model/Graph/SdfWorldPackage.Resolve.cs`,
  `src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-resolve.comp.hlsl`, and the
  history's generated constants.
- *Needs.* Nothing. The owed reading on temporal supersampling is the
  lane's own first day.
- *Model.* Opus `specialist`. The split gets an adversarial review, because
  it orders GPU reads and writes of one buffer.
- *Finish line.* With the device pool's budget lowered by hand
  (`host.memory`), a frame that adds work details still records and
  presents. `pipeline.inspect world` reads the history at about 232 bytes a
  pixel. A walk with camera motion, a newly revealed surface, a cut and a
  fresh history shows no new ghosting or shimmer in presented frames. The
  resolve's GPU time is reported before and after.

**`bench`: the memory cap as an engine input, and a benchmark that reads
the bar as ruled.**

- *Outcome.* A device profile in the standard package names a device's
  quality table and its memory bar, and the engine selects one by the
  adapter it runs on. The owner's 8 GB cap for the RTX 4070 then becomes
  the limit the engine admits against, where it is lower than the operating
  system's budget less a tenth, which is the limit today. The benchmark
  applies the profile before its first chapter and reports it. Last, the
  route driver reads what the seat's client was delivered, so the route
  runs against a remote authority (decision 1).
- *Owns.* `worlds/standard/quality.puck`, the quality rows in
  `src/Puck.World.Schema`, the boot code in `src/Puck.World` that selects a
  profile and sets the device pool's budget,
  `src/Puck.World/WorldSeatDriver.cs`,
  `src/Puck.World/WorldBenchmarkCommandModule*.cs` and
  `src/Puck.Cli.Bench/Benchmark`.
- *Needs.* Nothing. No device name enters engine code: the names and the
  figures are the standard package's data.
- *Model.* Sonnet `implementer`.
- *Finish line.* On the lead machine `host.memory` reads the device pool's
  limit at 8 GB, and selecting each other device's profile changes the
  quality levers and the cap. A benchmark report names the profile, the
  table, the cap, the backend, the size and the topology. A headless route
  against an authority in a second process arrives in every chapter, and
  its report gives the endpoint, local simulation, delivery and rendering
  apart. The run on the Azure deployment is a request to the lead.

**`colour`: scene light converted once.**

- *Outcome.* Scene light is converted to display values exactly once, in
  each view's own graph, and every place is retuned in the same merge
  (decision 5). The lane builds its two instruments first: an authored grey
  card with luminance patches, and a readback of the presented image in the
  display's format.
- *Owns.* `src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl`,
  `src/Puck.Shaders/Assets/Shaders/Runtime/display-encode.frag.hlsl`,
  `src/Puck.Shaders.Model/Graph/SdfWorldPackage.Bloom.cs` and the view's
  last stage, the image kinds in `RenderGraphPackages.cs`,
  `src/Puck.Maths/HexColor.cs` with the two colour types and their parse
  sites, and the exposure, light and tone values of `worlds/standard` and
  `worlds/debug`.
- *Needs.* Nothing.
- *Model.* Opus `specialist`. The retune gets a look critic, for at most
  two rounds a place.
- *Finish line.* The grey card reads 118 of 255 through every path a view
  takes. Each chapter of the route meets its reference in presented frames.
  The hall's hand compensation is gone. The encode pass's GPU cost at 2560
  by 1440 is in the hand-back. The HDR outputs wait for an HDR display.

**`shadows`: one shadow answer for every receiver.**

- *Outcome.* A light that emits nothing holds no map, and the brightest
  directional light holds the near and far maps (decision 6, step 1). A
  point that no map contains takes the probes' answer for that light
  (step 2). Nested views give up only what an authored table says, and the
  two constants that switch their shadows and ambient occlusion off are
  deleted (decision 14). The cascade set's shape is fixed as one indexed
  list, which the far shadow map then joins.
- *Owns.* `src/Puck.World.Client/WorldShadowAllocator*.cs`,
  `src/Puck.SdfVm/SdfMeshShadowCascades.cs`, the shadow slots in
  `src/Puck.SignedDistance`, `passes/sdf-surface-stage.hlsli`,
  `shade/sdf-light.hlsli`, `WorldSessionSceneEmitter.ReducedQuality`,
  `WorldScreenBinder.CameraViewQuality`, and the nested table's rows in the
  quality presets.
- *Needs.* Nothing.
- *Model.* Opus `specialist`.
- *Finish line.* At noon the second light's shadow pass draws nothing.
  Through an exchange of sun and moon, run both ways, no presented frame is
  without shadow maps. Glass, a view with no maps, and a point past the
  last map are each checked on their own. A lit exhibit through a full-size
  portal matches the direct view, and neither constant remains. The GPU
  cost that shadows add to nested views is in the hand-back.

**`withdrawn`: no picture a viewer is no longer allowed.**

- *Outcome.* The viewer's disclosure is checked at the two places a picture
  can reach it: where a picture is bound to a reader, and where a finished
  job installs its result. The viewer's old output is gone on the first
  frame delivered after its access changes (decision 2 with decision 14).
- *Owns.* `src/Puck.World.Client/WorldSessionRoute.cs`, the binding and
  pausing code of `WorldViewGraphHost`,
  `src/Puck.World/WorldScreenBinder.Availability.cs` and `.Views.cs`,
  `src/Puck.Shaders/Graph/RenderGraphRuntime.ExternalReads.cs`, the held
  reads of `RenderGraphScheduler`, and the point where a finished
  background job installs its result.
- *Needs.* Nothing.
- *Model.* Opus `specialist`, with an adversarial review: it is a trust
  boundary.
- *Finish line.* Two cases, each read on the first frame delivered, with a
  detail only the full view shows. A full view is narrowed to a private one
  on a frame its refresh is held. A viewer is revoked while a second viewer
  keeps the world alive. In both, a job submitted before the change
  installs nothing afterwards, and the second viewer keeps its picture.

**`terrain`: a sector at its declared spacing, and a relief that can be
masked.**

- *Outcome.* Gap 1's report and bound fix come first and land on their
  own. Then gap 4: the relief mask's steps A to C.
- *Owns.* `src/Puck.World.Client/WorldPlacementStamper*.cs`,
  `src/Puck.World.Authoring/Authoring/CreationStampEmitter*.cs`, the noise
  record in `CreationDocument.cs`, the mask in `src/Puck.SignedDistance`
  (the builder, validation and both interpreters), the kernel's arm, and
  the lake's mask in `worlds/standard/terrain.puck`,
  `worlds/debug/valley.puck` and `worlds/debug/shapes.puck`.
- *Needs.* Nothing. Step B's kernel agreement on both backends is a
  request to the lead.
- *Model.* Opus `specialist`. The mask's instruction gets an adversarial
  review.
- *Finish line.* The report names what set a sector's cell, and the sector
  then bakes at its declared spacing. The mask's own checks hold
  ([its steps](terrain-pass.md#steps-to-build-it)): outside every band the
  masked and unmasked fields are bit-identical, and the lake's bowl stands
  where its shapes put it.

**`cutter`: a cutter that moves re-carves what it cuts.**

- *Outcome.* Gap 8's step G1: a cutter is an operand of the group it cuts,
  the group is planned again when the cutter moves, and the settle rebake
  is deleted.
- *Owns.* `src/Puck.World.Authoring/Authoring/CreationMotionPlan.cs`, the
  pooled emitter (`WorldCreationInstances.Emit.cs`),
  `SdfMeshGeometryBuilder`, `SdfMeshSettle.cs` and
  `SdfMeshPathBake.Settle.cs`, which go, and the group state only they
  read.
- *Needs.* Nothing.
- *Model.* Opus `specialist`.
- *Finish line.* G1's checks hold
  ([its steps](moving-blends.md#steps-each-with-its-check)): the probe
  validates with the cutter as an operand, the audit at two poses prints
  two geometry keys and a cut that moved, and a running World shows the
  operand's re-plans and no settle counter.

**`floor`: a shape in reach always has a mesh.**

- *Outcome.* First the counters that size a floor: triangles drawn for each
  level and tier, and bytes no pass selected (decision 8). Then the floor
  of the streaming plan's step 5, which is gap 3's first half.
- *Owns.* `src/Puck.SdfVm/SdfWorldTables.MeshRegion.cs`,
  `SdfMeshRegionArena.cs`, `SdfMeshTier.cs`, the tier planning in
  `SdfMeshPathBake.Tiers.cs` and `.Residency.cs`, and the residency's
  counters.
- *Needs.* Nothing.
- *Model.* Opus `specialist`.
- *Finish line.* The counters print on the route. A burst of cold arrivals
  shows no presented frame with a missing surface, and frames in which a
  group in view had no floor are counted. Floor bytes are reported by class
  at the hero spawn, the painting wing and the garden.

**`water`: depth over the bed, the eye in water, and flow that varies.**

- *Outcome.* Water reads as shallow or deep over its bed (gap 5). A camera
  inside it sees depth (gap 9). A flow layer's speed varies over its
  surface (gap 6). A field row can be marked as simulation only (gap 14).
  Gap 5's comparison comes first and decides its design.
- *Owns.* `passes/sdf-glass-stage.hlsli`, `frame/sdf-glass.hlsli`,
  `src/Puck.World.Authoring/Authoring/PaletteLayersDocument.cs`,
  `src/Puck.World.Client/WorldFieldEmitter.cs`, the row's flag in
  `src/Puck.Physics/Fields/FieldLattice.cs`, and
  `worlds/standard/water.puck`.
- *Needs.* A GPU grant for gap 5's comparison.
- *Model.* Codex, as a fresh read of the glass pass.
- *Finish line.* Presented frames over the lake's bowl show the stones in
  the shallows and a darker deep end. A dive in the pool shows colour that
  deepens with distance. The lake's sill runs faster than its middle. The
  hidden chain under the valley bakes and draws nothing while the water
  still levels against its bed.

**`contour`: each failing bake named, and the first defect fixed.**

- *Outcome.* For every shape that fails the mesh audit, the report says
  which of gap 7's four defects it shows and whether its cell was ever
  flagged for finer sampling. If the failing cells were never flagged, the
  fix is the trigger. Otherwise it is the contour rule for a cell that
  cannot resolve its faces, inside the existing refinement.
- *Owns.* The contour and refinement code of
  `src/Puck.SdfVm/SdfMeshPathBake.cs`, and the report of `puck mesh audit`.
- *Needs.* Nothing.
- *Model.* Sonnet `implementer` for the report. The lead moves the lane to
  a `specialist` if the fix is the contour rule.
- *Finish line.* The report lists each failing shape with its defect and
  its flag. After the fix, no class of defect is worse over the stored
  meshes of the world, and the exhibits it names pass their audit, seen
  from the sides that failed.

Where lanes meet, and who merges first:

- **The quality presets** (`worlds/standard/quality.puck` and their rows in
  the schema): `bench`, then `shadows`, then `colour`.
- **The view's graph** (`SdfWorldPackage.cs`): `history`, then `views`,
  then `colour`, which merges last because it retunes every place.
- **The shading includes** (`shade/sdf-light.hlsli` and what the views pass
  includes): `shadows` lands its receiver rule first, and `views` merges
  the tip before it restructures the lookup.
- **The glass pass** (`passes/sdf-glass-stage.hlsli`): `shadows`, then
  `water`.
- **The bake** (`SdfMeshPathBake` and the group's state): `terrain`'s bound
  fix, then `cutter`, then `contour`, then `floor`. `cutter` and `contour`
  both add to `puck mesh audit`, in that order.
- **The benchmark's report**: `bench`, then `frame`.

**Read-only work beside the wave.** These hold no build and no GPU, and
start as a model is free.

| Work | Outcome | Model |
|---|---|---|
| The first bet's memo | Every input `SdfWorldResidency.Prepare` reads on the client has a named change record, or is listed as having none ([the first bet](#the-first-bet-stands-on-the-clients-records)) | Fable `principal`, or Astra |
| Terrain research | A research round across the families on seams between tiles, how tiles scale, and far shadows. The terrain pass's plan holds its findings | The lead's research round |
| The stretched primitive's solver | An exhibit and a failing measurement for each of gap 11's two effects, then the design of the closest-point solve with its certificate | Fable `principal`. Any proof that skips work gets an adversarial review |
| The fourth bet's memo | What a shared product's name holds, the receiver's check for each attribute, and how fetching is priced against deriving | Astra, or Fable `principal` |

### The second wave

Each lane starts when what unlocks it has landed. A lane whose unlock is
"a free slot" needs nothing but capacity, and those are taken in the order
listed. The model classes are the first wave's: Opus where an invariant
carries the change, Sonnet where the result is specified.

| Lane | Outcome | Unlocked by | Owning plan | Model |
|---|---|---|---|---|
| Far shadows (gap 3) | Ridges cast shadows on the far walls, from a map that is refreshed only when the sun or a static draw changes | `shadows`: the far map is one more entry in its cascade set | [The terrain pass](terrain-pass.md#order-of-work), step 7 | Opus |
| Terrain programs (gap 2) | The terrain template with slope, altitude, curvature and breakup masks on every wall | `colour` | [The terrain pass](terrain-pass.md#slope-altitude-and-curvature-materials), step 6 | Opus, with a look critic |
| The capture order | Decision 2's order: upkeep, the list sealed after the cameras, readers found while captures run, minimums in place of the hidden render, state keyed by view | `withdrawn`, which changes the same binding code. `frame`'s breakdown ranks its smaller cuts and does not hold it | [Streaming and memory](streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render) | Opus |
| The background allowance | The allowance follows the GPU time the frame leaves free, bake jobs are cut to it, and lighting is granted work for each second (decision 4, steps 1 to 3) | A free slot. The first to take one: at 20 frames a second the lighting gets a third of its work | [Illumination](illumination.md) | Opus |
| Casters culled by reader | Uploads only for instances that changed, the compacted command list, and shadow casters culled in every map (decision 11, steps 1 to 3) | `shadows`, for the cascade set, and `floor`, which changes how a draw names its mesh | [Visibility](visibility.md) | Opus |
| The overlay | The interface overlay as clipped geometry with text from an atlas (decision 15) | A free slot | [The UI overhaul](ui.md) | Sonnet |
| The relief operation and its caps (gaps 1 and 4) | The terrain's relief operation carries the mask, and relief and smooth blends are capped relative to the stamp | `terrain` | [The terrain pass](terrain-pass.md#order-of-work), steps 3 and 4 | Opus |
| The moving cutter, continued (gap 8) | Relief with a cutter (G2), source tiles keyed by the cutter's reach (G3), then the glacier retreating by a cut (G4) | `cutter`. G4 also needs `terrain`, for the cradle's mask | [Moving blends](moving-blends.md#steps-each-with-its-check) | Opus |
| Eviction and the backing | Memory is given back under pressure, judged in physical bytes | `floor` | [Streaming and memory](streaming-and-memory.md#the-build-order), steps 7, 4 and 8 | Opus |
| One shading path | Authored shading programs are the only path, and the fixed layers are deleted (decision 13) | `colour` and `views` | [Author shaders](author-shaders.md) | Opus |
| Haze as an authored range (gap 13) | Changing the draw distance changes no nearer pixel | `colour` | [The terrain pass](terrain-pass.md#distance), step 2 | Sonnet |
| The paint on a blended seam (gap 10) | A painted marking stays fixed to a surface that bends | `water` and `shadows` | [Moving blends](moving-blends.md), step 4 | Opus |
| The particle pool (gap 15) | Spray at the falls and a splash in the plunge pocket | `water` | [Water](water.md#w3-the-presentation-particle-pool) | Sonnet |
| The stretched primitive (gap 11) | A true distance for a stretched capsule, cone and torus | Its design | [Rendering](rendering.md#bake-quality) | Opus |
| Shared allocation | The allocation both backends share is cut at the sources a trace names | `vkalloc`, whose trace says what is left | [Job system](open-items.md#engine-job-system) | Sonnet |
| Keys by stage | An edit to the simplifier rebakes with no field sampling (decision 10) | `cutter` and `contour`, which change the same bake code | [Bake proportional to change](open-items.md#bake-proportional-to-change) | Opus |
| Where the tick runs | A design that weighs the arrangements of decision 3, each with its time from input to display | `frame`'s cost of one more world | [The job system plan](engine-job-system.md#open-questions) | Fable |
| The shadow filter in metres | A prototype behind a switch, compared with a brute-force filter (decision 6) | `shadows` | [Transmission](transmission.md#implementation-steps) | Opus |
| A record for every present | Whether each frame was shown, skipped or dropped, and the time from input to display (decision 15) | The research on display timing, which is owed | [The benchmark](benchmark.md) | Sonnet |
| The bets' prototypes | The second bet after the capture order's minimums. The fifth after `floor`. The first only if `frame`'s breakdown shows the cost | As named | [The bets](#the-bets) | As each bet's memo names |

The job system's own order (bounded waits, then the thread pool, then the
tick's partitions) runs beside both waves on
[its plan](engine-job-system.md#6-build-order) and is not repeated here.

### After the second wave

Each item names the landing it needs. None waits for a wave to close.

- **One instance table** (decision 11): commands grouped by geometry and
  level, and the instance rows read once for each world. It needs the
  compacted command list.
- **One chain of meshes** (decision 8). It needs `floor`'s counters and the
  floor.
- **Light stored for each group of sources, with emitting surfaces lit as
  areas** (decision 7). It needs the background allowance. Shared surface
  lighting and one pool across worlds follow it.
- **The lighting's work list on the GPU, and the early hand-off**
  (decision 4). They need the background allowance, and the hand-off needs
  the job system's rework of waits.
- **Motion and placement at zone scale, and terrain tiles** (decision 12).
  They need the terrain research and the streaming plan's source tiles.
- **The rest of the nested-view work** (decision 14): windows fitted to
  their visible glass, glass by coverage, and results reused between
  viewers. The last needs `withdrawn`.
- **The present queue's admission rule and machine frames at the machine's
  rate** (decision 15). They need the record for every present.
- **The third, sixth, seventh and eighth bets**, each when
  [its row](#the-bets) says.
- **The bake's first speed fix** (decision 10). It needs one cold bake that
  records what limits it, taken after keys by stage.

**Then the other devices.** Testing on the RTX 2060, the Steam Deck and
an HDR display starts when the RTX 4070 meets both bars. By then each
device has its authored table, its memory bar and its budget in counted
work, so the first session on a device is a comparison against targets
and not a first look.

## Who decides what

Most choices on this page are settled by the code, a measurement or a
recorded ruling, and need no call. Looks belong to authors. A short list
belongs to the owner.

### Looks are authored

A look value is an authored parameter with a neutral engine default
([AGENTS.md](../../AGENTS.md#puck-is-pre-release), "Authors rule"). The
standard package chooses the values it wants and any world overrides
them. The owner corrects a look and is never asked to choose one. No step
of the build order waits for a look.

This plan touches these looks, and each has or gets an authored home: the
tone curve, exposure and its change between places, a light's softness
and what a light with no authored size does,
the widest soft shadow edge, how far shadows reach and how far a distant
shadow may lag, the night, the indirect light's allowances, the quality
tables for each device, what a nested view gives up, the detail budgets,
how ground cover thins with distance, and how much of its own exposure a
window's world applies. [The step designs](rendering-research/step-designs.md#where-each-look-is-authored)
name the authored setting for each and say which exist today.

One default is not neutral and stays. The detail budgets (one pixel of
error for a level, one texel for a shadow map) are the owner's recorded
rulings. They stay the engine's defaults, and an authored table may
differ.

One default is not neutral and goes. A directional light with no authored
angular size takes a soft edge the engine chose
(`SdfLights.DefaultPenumbraSlope`). That is a look held in the engine. With
the `shadows` lane, a light with no authored size is a point source, and
its shadow's edge is as hard as the map's filter allows. The standard
package authors the soft edge it wants on its own lights, and an author's
explicit size is honoured as it is today.

### What the owner settled

1. **How a shared product is named and trusted.** Confirmed as
   [above](#how-a-shared-product-is-named-and-trusted): a local cache key
   computed from everything, a shared name from meaning, trust from a
   check at the receiver's own tolerance, and a local floor on every
   device. The decision register records it
   ([engine design](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
2. **The memory caps.** Confirmed: 8 GB of GPU memory on the RTX 4070 and
   4.5 GB on the RTX 2060, leaving room on each card for the rest of the
   desktop, and the Steam Deck's 10 GB for the process and the GPU
   together. Each is a cap on what is resident around the viewer, never on
   a world's size. It is the device's: two seats on one machine share it.
3. **Render scale.** Delegated to the lead, adjustable as feedback
   arrives. The frame bar is measured at a native render scale on every
   device, so meeting it says something about the engine. Render scale is
   an authored setting in each device's quality table, so authors and the
   standard package may lower it for players; that choice does not count
   as the engine meeting its bar.
4. **Who draws a world for a viewer that may not receive its data.** Three
   tiers, authored per portal or screen, and never per-observer server
   rendering ([three ways worlds compose](#three-ways-worlds-compose)):
   published pictures first, the visible part with secrets withheld as the
   target ([the eighth bet](#the-bets)), and layered pictures when the
   geometry itself is secret.

Three things are not questions. The size the bar is read at, the two
topologies and the order of devices are ruled. Sharpening stays ahead of
bloom, as the register rules. And the hardware is not needed until the
RTX 4070 meets its bars.

No owner question blocks a lane of either wave. One design question is
open and is not the owner's yet: where the tick runs. The job-system plan
owns it, the first wave measures what it needs, and it reaches the owner
only if the design it settles on adds latency for the world the player
acts in ([decision 3](#3-shorten-the-tick-and-settle-where-it-runs)).

## What would change the plan

The results most likely to change it, and what would happen then. The
full list is in [the step designs](rendering-research/step-designs.md#what-would-change-the-answer).

- **The split of the shading pass shows the probe lookup is not its
  largest part.** The `views` lane cuts whatever is, and the shading row's
  share is set again from the split.
- **Most of produce's 26 ms turns out to be waiting for the GPU.** The
  capture order then saves less than its row claims, and the GPU rows
  (shading, shadow maps, the resolve) come ahead of it.
- **The breakdown of produce names a part no decision covers.** Decision
  2's order follows the breakdown, not this page.
- **The lift's steps come from sending whole frames of state.** The fix is
  delivery that follows the rows that changed, in the transfers packet, and
  it reaches every crossing and not only the lift.
- **The tick's breakdown shows the planned splits save little.** The
  tick's serial fixes come first.
- **One more world in frame costs most of a step.** Where the tick runs
  stops being a later design and becomes the job-system plan's next step.
- **The GPU rows miss their shares.** The table leaves half a millisecond
  of margin, so the first row that misses is made up in another. If none
  can, the split is set again from the readings.
- **Light stored for each source costs more memory than the probe row
  allows.** It narrows to the directional lights and the sky.
- **Measuring every mesh in the chain costs too much bake time.** The
  chain stops at the floor.
- **A cold bake does not fit a weak device.** The floor bake is cut until
  it does, because every device keeps a local floor. Fetched products
  shorten the wait, and the fourth and fifth bets move up.

## Where the rest is

[The step designs](rendering-research/step-designs.md) hold, for each
decision, the steps with their order, the options that were not taken,
and the reasoning in full. They also hold the table of missing
measurements, what each item's run should show, the properties the order rests
on and what enforces each, the outside techniques that were considered
and not taken, the research still owed, and the sources.
