# Iteration

An author's edit shows in the running World in seconds
([the owner's ruling](../../AGENTS.md#the-owners-velocity-rulings)). This plan
holds the loops an author and a developer go round, how each is measured, what
makes each slow today, and the order of the work that makes them fast.

## The loops

| Loop | The edit | What it should cost |
|---|---|---|
| Look value | A colour, a shade program's parameter, a light, exposure, a style | An appearance upload: no rebake, no recompile of what did not change, lighting relit only where the value reaches |
| One creation's form | A shape moved, resized or added in one creation | A rebake of that creation's groups alone, a coarse tier first and finer tiers after, lighting relit only around it |
| A place's layout | A placement moved, added or removed | The same, for the placements touched |
| Engine code | A C# or HLSL edit | Shaders reload live. A method-body edit in a project the watch lists applies to the running World, and every product of that code derives again ([edit to running](../development/contributing.md#edit-to-running)). Any other edit restarts, and the restart reuses every baked mesh and settled light the edit did not invalidate |
| A cold machine | An empty store | About a minute per backend to bake the debug world's meshes, lighting settled soon after |

A reload keeps the live value of every state row the edited source did not
change, and re-seeds the rows it did, so an author keeps their place on a save
(following from the rapid-iteration ruling); `world.reset` and `world.load`
restart state from the file.

## How each is measured

The World times every loop itself (`WorldIterationClock`, read with
`world.iteration`; [the reference](../reference/commands.md#edit-loops)). Each
loop is timed from the edit to the first produced frame that finds each of
six stages true:

- `first`: a frame is produced at all. For the boot this is the process start,
  the document load and the renderer's composition, so `worst-frame` (the
  longest wait between produced frames until `shaded`) never counts them as a
  held frame.
- `frame`: the frame renders the edited document, or a reloaded kernel, with
  the program its content calls for. A look value shows here.
- `shaded`: every shading program the frame runs draws with its own kernel
  (`SdfWorldResidency.ShadesWhole`). An edited or added shading program draws
  as a material with no program until the specialization that holds it
  publishes; the frame never falls back to the interpreter
  ([the decision](../decisions/rendering.md#authored-shading)).
- `shown`: every mesh group draws its own geometry at some tier, with no
  stand-in left from before the edit and no hole. A changed form shows whole
  here.
- `meshes`: the bakes have settled at their demanded detail and stand in the
  mesh region.
- `lit`: the irradiance volume has converged
  (`SdfMeshIrradiance.Converged`): nothing is left to trace or relight, or,
  under a lighting that changes every frame such as a day clock, its geometry
  is settled and its relight keeps every probe within its age bound. This is
  the settled frame.

The boot is a loop timed from process start; `world.reload` (by hand or from
`world.watch`), `world.shaders.reload` and a hot-reload edit (from the
runtime's application of the update) begin loops themselves, and a script
names any other edit with `world.iteration begin <name>` on the line before
it. A settled loop prints `[world.iteration] …` on stderr, so a lane reads the
loop it changed and the batch reads every loop from the World's log.
`world.wait iteration <seconds>` holds a script until the latest loop
settles. `puck bench loops` is the maintained reading: it boots a windowed
World on a private copy of the worlds tree and reports the boot, a `.puck`
edit, its revert and a restart, each by stage and as its own figure
([the reference](../reference/cli.md#puck-bench-loops)). Between the stages,
`host.log` lines carry seconds from the process's start, so a wait shows as
the gap between two lines.

A watched save starts its loop when the watch submits the reload, after its
quiet period (`WorldViewGraphHost.WatchDebounceMilliseconds`), so the time from
the save itself is that much longer. A restart after an engine edit adds the
World's build before its boot loop starts; `puck_world_start` reports the
build's seconds beside it.

### The baseline

The debug world on Direct3D 12, windowed, `--devices none`, each loop run once
with a scripted edit on the RTX 4070 lead machine. Seconds from the edit
(from process start for a boot) to each stage; the commit that adds this table
carries the commands and the World's own lines. It is still the only windowed
reading of every loop, and most of its rows no longer describe the code: the
next list says which. The lead's quiet window reads every loop again, and that
reading replaces the table.

| Loop | The edit | frame | shown | meshes | lit |
|---|---|---|---|---|---|
| Look: palette colour, live | `world.row.set creations cube-1m document.palette[0].color …` | 1.4 | 20.9 | 20.9 | not reached |
| Look: palette colour, saved | the same colour in `debug.puck`, then `world.reload` | 8.8 | 8.8 | 11.4 | not reached |
| Look: exposure | `world.row.set render exposure.maximum 12` | 1.1 | 1.1 | 1.6 | not reached |
| Look: light | `world.row.set render lighting.lights[name=key].color …` | 1.2 | 1.2 | 1.7 | not reached |
| One creation's form | `cube-1m`'s box scale in `debug.puck`, then `world.reload` | 9.7 | 12.6 | 12.6 | not reached |
| A placement's layout | `cube-1m`'s placement moved 2 m, then `world.reload` | 14.5 | 14.5 | 17.1 | not reached |
| Shader reload | a constant in `sdf-composite.comp.hlsl`, then `world.shaders.reload` of the source tree | 3.2 | 3.2 | 48.6 | not reached |
| Restart, store reused | World restarted after an SdfVm edit the bake identity does not read | 47.0 | 55.0 | 55.2 | not reached |
| Boot, every mesh missed | first boot after an edit to `SdfMeshPathBake`, which the bake identity reads | 53.2 | 322.0 | 322.3 | not reached |
| Cold boot, empty cache | fresh `--cache-dir`, on the merged tip | 51.0 | 314.3 | 314.5 | not reached |

What has changed since, row by row:

- **The look rows.** A palette colour, an exposure and a light edit now build
  no program (`builds=0`) and begin no bake, counted in a windowed World. A
  rebuild hands the builder the program it replaces, and an emission whose
  field inputs are unchanged takes that program's field derivation
  (`SdfProgram.Of`, `SdfProgram.SharesField`). The live colour's 20.9 seconds
  to `shown` were dealt placements the client had never drawn, which now show
  on their own tick (`WorldPlacementResponse.ComparePlacements`). The seconds
  are unread.
- **The saved-edit rows.** A one-colour file edit now recompiles one source,
  merges only the root members it writes and installs as a presentation
  install with no rule compile ([the live edit path](#the-live-edit-path-step-3)).
  Those are headless counts; the seconds are unread.
- **The restart and missed-mesh rows.** An engine edit outside the bake's
  stages now keeps every stored mesh, and an edit inside them re-runs only the
  stages it reaches (step 2). An edit to the field or the bake kernel still
  rebakes every group.
- **The cold boot.** A free-running cold bake now takes 114 seconds on
  Direct3D 12 and 123 on Vulkan, against the table's 268, from the
  `[sdf.mesh-bake] done` line of one windowed boot each on a shared machine.
  Those figures are indicative; the quiet reading is owed.
- **The kernel reload** no longer waits for the device to go idle when it
  replaces pipelines; the replaced ones retire once the next upload completes.
  Its seconds are unread.
- **The `lit` column.** Most of step 4 has been built since; the stage is
  unread.

What the readings show:

- **`lit` is reached, slowly.** Under the day clock the relight has to keep
  every probe within its age bound, about 5,500 probes a second over the
  debug world's 73,728; the clean window read `lit` 117 to 181 seconds after
  a boot, with the relight at 1,300 to 1,800 probes a second
  (`[sdf.mesh-gi] behind`). The causes and fixes are below.
- **A saved edit spent most of its time before the first frame.** Each
  `world.reload` took 9 to 15 seconds to reach a frame, though the rebake after
  it took 0 to 3. Compiling, composing, diffing and installing the document was
  the cost; the counts above say how much of it is gone.
- **A kernel reload shows in 3 seconds and finishes in 49.** The first changed
  pipeline installs quickly; the request stays pending until every carried
  source in the tree has compiled.
- **A restart spent most of its wait before the first frame naming its own
  code and compiling its sources again.** `host.log` lines carry seconds from
  the process's start, so the boot's wait reads as the gaps between them. On a
  warm store the definition line came 19 to 28 seconds in, with every one of
  the debug world's 89 sources compiled again, and a sampled trace of a boot
  that compiled nothing put half of what remained in one walk of the engine's
  IL (`CompiledWorldChunks.EngineBuild`). Three defects made it: the compile
  cache was keyed by the module version of every assembly the compiler's
  references reach, so any rebuild of Abstractions, Hosting, Maths, Physics or
  State emptied it; its 256 MiB bound held less than one boot's entries, so a
  boot evicted its own; and every product's code identity walked its closure
  in every process. Each is fixed: compiles are keyed by the code the compiler
  reaches, the bound holds several builds' sets, and every code identity is
  remembered for its build ([the decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
  Counted on headless boots over a private cache: a warm boot compiles none,
  and a rebuild after an edit to `Puck.SdfVm` or `Puck.Hosting` compiles none.
  What remains before the first frame is the authority's build, the screen
  sessions' destination instances, opened one at a time, and the renderer's
  composition; their quiet split is owed.
- **The session windows a colour edit seemed to rebuild were the nesting
  depth.** `world.row.set views nestingDepth 0` releases every session window
  (`WorldScreenBinder.ReconcileNesting`, under the reason `source changed`), and
  the edit was read in the same session as that toggle. A colour edit after the
  same toggle, and a saved edit and its revert without it, released no window
  and settled (`meshes` 0.8 to 11 seconds) in a windowed World.
- **Lighting waited on a stale scene and on the queue's round trip.** The
  irradiance volume paced every scene rebuild by the installed scene's trace,
  so a boot's first scene, built from the draws landed at that moment, held the
  settled one back while the whole window traced it (57.9 seconds in one boot).
  The background GPU budget counted every job still in flight against the next
  frame, so the work ran one allowance per round trip through the queue: on a
  0.25 share it measured an occupancy of 0.09, with nearly every job held by
  the deadline. And the probe jobs were priced for the costliest job seen, so
  each filled its allowance and measured a third of it. The scene now rebuilds
  once the bake owes no static mesh, a job counts against the frame it queues
  ahead of (`SdfGpuWorkBudget.FramePassed`), and probe rates follow their mean.
  One loaded windowed boot reached `lit` at 94.6 seconds against 172.1 before.

## What makes the loops slow today

AGENTS.md sets two figures as forcing functions: about half a minute from an
engine C# or HLSL edit to seeing it in the running debug world, and about a
minute to bake the debug world's meshes from an empty store, per backend. The
latest readings against them:

| Loop | Latest reading | Against the figure |
|---|---|---|
| HLSL kernel edit, `world.shaders.reload` | 3.2 s to the first frame that draws the reloaded kernel (the baseline) | Within it |
| C# edit that hot reload applies | 0.88 s from the save to the applied revision; the World now times it as its own `hot-reload` loop, whose frame reading is owed | Within it |
| C# edit that needs a restart | 6.9 s of build, then 21.9 s to the first frame on a warm store, of which the compile and the code walks above were most; their removal's reading is owed | Over it |
| Cold bake, empty store | 114 s on Direct3D 12 and 123 s on Vulkan, free-running, on a shared machine | About twice over |

- **A restart is the C# loop's cost.** An edit hot reload cannot apply (a rude
  edit, or one in a project the watch does not list) rebuilds the World and
  boots it. The bake no longer adds to that for most edits, and the boot no
  longer compiles the world's sources or walks its code again (above). What
  remains is the authority's build, the destination instances the screen
  sessions open one at a time before the first frame, and the renderer's
  composition. `puck bench loops` reads the boot, a `.puck` edit, its revert
  and the restart, each as its own figure ([the reference](../reference/cli.md#puck-bench-loops)).
  The [edit-to-running packet](open-items.md#edit-to-running-hot-reload-and-boot)
  owns the restart, the build and their quiet timing.
- **A cold bake is latency-bound.** The bake kernel is about 2.4% of a cold
  bake's wall time, and the GPU is idle most of the time a job is in flight
  ([the measurement](field-programs/research-measure.md)). Under lockstep the
  CPU worker pool set the pace, with about a sixth of the wall time stopped for
  garbage collection and another sixth of the workers' samples waiting on the
  bake's own lock ([where the time goes](field-programs/research-bake-time.md)).
  Step 5 holds what is built and what is in flight.
- **The cold bake also paces releases.** The player channel's mesh cache sets
  are baked on the lead machine, one cold bake per backend and per device
  target, whenever the bake's identity moves
  ([the player build](runtime-and-delivery.md#the-player-build-and-its-channel)).
- **Lighting settled in minutes.** The irradiance scene was rebuilt as detail
  tiers landed and a retier relit the whole window; a first boot's main volume
  settled after nearly ten minutes. Step 4 holds what is built since.
- **An author's `.puck` edit still validates and delivers the whole world.**
  A saved file reaches the running World as a `world.reload` (by hand, or
  queued by `world.watch on`). The compile and composition now do work in
  proportion to the edit, including the first reload after a warm boot.
  Every install but a state-value one validates the whole
  candidate, the install clears the journal, and each client receives the
  whole definition again while the frame waits
  ([what is left](#what-is-left)). [The authoring loop](iteration/authoring-loop.md)
  maps that path through the compiler, the caches, the CLI, the World, the
  editor and the save back to source, and designs the loop that replaces it.

## Order of work

Step 1, the instrument ([how each loop is measured](#how-each-is-measured)),
and step 2, bake keys by stage, are built. `puck mesh identity --against`
reports which of sampling, contouring, simplification and landing an edit
reaches, and the bake resumes from the stored products of the stages it did
not reach.

3. **The live edit path.** A saved `.puck` reaches the running World as the
   smallest change it is, with look values as appearance uploads and form
   edits as a progressive rebake of only the creations touched. The door is
   built ([below](#the-live-edit-path-step-3)). What is left is the authoring
   loop's steps 3 to 8, in [its build order](iteration/authoring-loop.md#build-order),
   which depend on nothing outside that memo.
4. **Lighting from a stable proxy, relit incrementally, settled at boot with
   the whole GPU.** Built: the irradiance scene traces each static group's
   floor, so view retiers and the budget's bias leave it standing; a rebuild
   keeps the batch in flight; a probe keeps its local cube across a change that
   misses it; a change is a box hierarchy rather than one union box; rebuilds
   are paced by trace progress while the bake still owes static meshes, and a
   settled scene replaces a stale one at once; one GPU budget is shared across
   residencies by their share of the display, and fills one allowance a frame
   rather than one a round trip through the queue; probe jobs are priced at
   their mean; and a seen volume's first light takes the allowance an arriving
   bake takes
   ([the settle research's fixes](illumination/research-settle-time.md#the-ranked-fixes)).
   Left: light reused across boots, so a boot over an unchanged scene starts
   from the last settled light instead of no bounce (the volume's traced hits
   live on the device, so this needs a read-back of the hits and the relit
   cubes keyed by the scene's static draws and the layout); a session that
   renders the boot world sharing the world's meshes instead of holding its own
   copy (that research's fix 3, a streaming change); uploading only the top
   level of a scene change (its fix 9); and a relight driven by which probes'
   inputs moved, which a material edit needs. *Check:* the boot loop's
   `world.iteration` line reports `lit` within seconds of `meshes` on the debug
   world in the quiet window.
5. **The cold bake fed.** Built: an arriving group's bake work takes its own
   allowance of the GPU the frame leaves idle, the bake starts once its own two
   kernels compile, only groups arriving in view plan a coarse first tier, the
   contour and crossing stages rent their scratch, an in-flight bake is
   shared across residencies, store entries stream to their file through a
   background writer, and the take walks only groups offering work. What it
   still owes is [below](#the-cold-bakes-steps).
   *Check:* a free-running cold boot's `[sdf.mesh-bake] done` line on each
   backend, read in the lead's quiet window, against the minute figure.

The missing look tools, authored large terrain form and a stylized surface
detail layer, are built alongside, each judged through these loops.

## The cold bake's steps

Step 5 above is built; these are what the cold bake still owes, in order.
[Open items](open-items.md#bake-proportional-to-change) holds the defects
beside them. The first candidate of step 3 changes the bake's shape and goes
ahead of further cuts to its CPU stages.

1. **Read staged reuse in the windowed debug world.** The stage products
   resume in a device-free replay; the windowed reading is owed. Start from an
   empty store, then boot with that store unchanged, with a simplification
   edit, and with a field-kernel edit. Compare bake time, checkpoint hit and
   miss counts, GPU sampling dispatches, and disk bytes. A simplification edit
   should load stored contours and dispatch no sampling. The field-kernel edit
   should rebake from sampling. The
   [derived-product decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)
   describes where the cache resumes.
2. **Read the cold bake under the live frame allowance.** Budgeted batches
   are cut to the allowance at a mean-following rate (`SdfMeshBakeGpuWork`),
   an arrival's lattices take their own allowance
   (`SdfGpuWorkBudget.OwedAllowance`), and held frames keep
   `SdfMeshPathBake.BatchTaps`. Read the cold bake held and free-running, and
   a cold arrival in play, on a quiet machine; split a lattice only if
   `over-allowance` shows the unsplit first lattice matters.
3. **The candidates.** A quiet cold bake recording worker activity, GPU wait
   per request, GPU timestamps and total time ranks what is left on the
   critical path. The first candidate changes the bake's shape and goes
   first; with it,
   [the audit's](state-of-the-art/audit.md#where-each-finding-went)
   coarse-to-fine pruning by interval bounds on the GPU. The candidates (GPU
   contouring is rejected as a second mesher):
   - *GPU sampling in one round trip per stage* (cutting ~20 round trips per
     group to ~4 by moving root search and gradients onto GPU with a per-stage
     sample cap): requires generating or reading `SdfSampledLattice`'s root
     search (bracket, tolerance, round limit, step) from **one single source**
     shared by CPU and GPU, verified on bracket, residual and thin-feature
     checks on both backends. Writing the root search again by hand in shader
     code would make two copies of one rule, whoever is asked to keep them
     equal. No source measures a root search on the GPU; Dreams ran a
     hierarchical GPU evaluator on a console, the nearest precedent.
   - *The Land stage on the job pool* (check the removed vertex first in
     `SdfMeshCollapse` before laying out the new fan, since the acceptance
     search makes about five attempts a collapse and `Measure` refuses half at
     the removed vertex's own samples, and share one error sample set between
     `Measure` and `MeasureReverse`, both in bake-cpu's collapse
     pricing; restrict `SdfSampledLattice.CloseSheets`'s second slit pass,
     which re-reads every measured fine sample's six neighbours, to neighbours
     of first-pass closings; schedule chunks on `JobPool` ordered by visibility
     demand before size, largest first only among chunks of equal priority;
     and split the serial tail's largest lattices into slabs contoured in
     parallel, the research's fix 4). A cheaper collapse whose meshes
     differ but audit as well is a valid cut: judge changed witnesses with the
     field audit at each rendered level, raw millimetre errors beside threshold
     counts. Try the cheaper collapse and a different error measure (attribute
     quadrics) inside `SdfMeshCollapse` before considering a replacement
     simplifier ([visibility V3b](visibility.md#the-build-order)). Not checked:
     Kapoulkine's clustering pipeline falling from about 9 minutes to under 3
     on the same class of fixes.
   - *Check:* `puck bench startup --bake` under `puck lease quiet` reads about
     a minute or less on each backend from its own empty store, and turning
     into a new place during a bake delivers the first visible surface no
     later than before. If the sampling fix is the one that lands, a group
     takes about four round trips.
4. **One shared cross-backend store (last, judged separately from cold bake).**
   Key meshes by authored inputs rather than backend bytecode, recording which
   backend derived each entry and accepting cross-backend entries by audit
   within recorded error (never byte equality; `SdfMeshCpuField.TapSpacing`
   already differs near creases, and native forest triangle counts differ by
   backend). This is the first use of
   [shared product naming and verification](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)
   (with fetch disclosure owned by
   [disclosure](disclosure.md#presentation-and-derived-product-disclosure)).
   - *Memo and three-part prototype:* write a memo fixing (a) what goes into a
     shared product's name (source program, authored parameters, byte-format
     version), (b) attribute-by-attribute verification against the analytic
     source at receiver tolerance/coverage and inconclusive handling, (c)
     format version computed from both writer and reader code so a
     layout-preserving reader change still moves it, and (d) unified pricing of
     fetching versus local derivation (keeping local store reuse distinct from
     machine-to-machine exchange). Prototype over every stored debug-world
     group at route tiers: (i) one desktop store read by both D3D12 and Vulkan
     in both directions (verified by audit, materials, seams and motion); (ii)
     one public gallery mesh fetched from another process beside one refused
     fetch, one permitted local miss, timing cold bake vs warm read vs fetch
     on a stated link, plus rejecting tampered products (removed part, swapped
     material id, flipped normal patch) and a layout-preserving reader-change
     refusal/recheck; (iii) RTX ↔ Steam Deck cross-vendor exchange in both
     directions once Deck hardware is on hand (unmeasured until then), because
     two graphics APIs on one card compare no vendors. Parts (i) and (ii) run on
     the desktop inside the bet's time box; part (iii) has its own box. Amend
     the decision register when adopted.
   - *Judged apart from the cold bake.* The cold-bake figure is each backend's
     own bake from an empty store with floors on, timed with the other
     backend's store absent. A shared store has three hazards: the same shader
     source can compile under different options or generated interfaces;
     whichever backend writes an entry first can hide which backend derived
     what is drawn; and a warm hit from the other backend's store is not a cold
     bake. The stage keys are local cache keys and are not affected.
   - *What it shows in the debug world:* the second backend's first frame loads
     the first backend's meshes, which pass verification, and the fetched mesh
     is ready sooner than it bakes.

## The live edit path (step 3)

An author saves a `.puck` file and the running World shows the change as the
smallest change it is. This section holds how that works, what enforces it and
what is left. [The authoring loop](iteration/authoring-loop.md) owns the
compile in front of this path and the save behind it.

### The parts

Most of the path existed before this step under other names; the step joins
them.

| Capability | Mechanism |
|---|---|
| Follow a source and its imports for changes | `WorldSourceWatch`, driven by `world.watch` |
| Read the source again and install it | `WorldReload`, the `world.reload` verb |
| Describe a document change by key, and prove it | `WorldReloadDelta.Between`: the root members whose digests moved (`WorldDefinitionMembers`), each diffed and proved by `WorldDocumentBasis.Diff` and `TryMerge` |
| Merge a source change over the session's own edits | `WorldReloadDelta.TryMergeOver` over `WorldDocumentBasis.MergeThree` |
| Keep what a change did not touch | `WorldDocumentWalker.Share`: the new document holds the old one's own object for every member and keyed row the patch leaves unchanged |
| Install a small change without rebuilding the simulation | `WorldMutation` through `WorldDocument.InstallKindOf` |
| Emit again only the placements that changed | `WorldStaticPalettes.Emissions`, keyed by the placement row and the creation |
| Upload only the program words that changed | `SdfProgramDelta` |
| Bake again only the groups whose form changed | `SdfMeshGeometry`, the key `SdfMeshPathBake` keeps a group by, and `SdfMeshLibrary` |
| Show something before the detail lands | A group's floor (`SdfMeshPathBake`, `SdfMeshBakeGroup.Shape`) |
| Relight only what changed | `SdfMeshIrradiance`: its static scene key for geometry, its materials key for what the scene's materials reflect |
| Run only the shading programs a frame names | The `shades` table and specialization by program set ([authored shading](../../.claude/skills/rendering/references/authored-shading.md)) |

### The diff, and why no table lists consequences

Two revisions of the source are compared once, by content, member by member:
the root members whose canonical bytes moved (`WorldDefinitionMembers`) are
diffed by `WorldDocumentBasis.Diff`, and `WorldDocumentWalker.Share` builds the
next revision so that everything the patch does not name is the last
revision's own object. The change is then merged over the live document, which
keeps the live document's own object wherever the change leaves it equal.
After that, whether a row or a member changed is an identity test.

Every product downstream is derived from particular rows and members and is
kept under their identity, or under a key hashed from exactly what the product
reads. The consequence of an edit is therefore computed where the product is
made, from what the product is. Nothing lists which fields are look values and
which are form.

| The author changes | What notices | What follows |
|---|---|---|
| A palette colour, a material value, a style | The creation row is a new object, so its placements are emitted again. Their groups hash to the same `SdfMeshGeometry` | Changed program words upload and no group bakes. The irradiance volume relights, because its materials key moved, and traces nothing |
| A shade program's parameter | The same, through the material's shade reference | The same. Kernels compile only when the set of programs a frame names changes |
| A light, exposure, tone | The render section is a new object; the frame carries its values | A value upload, and a relight where the lighting key reads the value |
| A shape in one creation | That creation's groups hash to another `SdfMeshGeometry` | Those groups bake, floor first. Every other group is kept or taken from the library |
| A placement moved, added or removed | A rigid copy changes one matrix; a placement baked in world space changes that group's geometry | No bake for a copy, one group's bake otherwise. The irradiance scene builds only the geometry new to it |
| A state row's declaration or seed | The row is a new object | That row is seeded again. Every other row keeps its live value |
| A rule, a kit, anything the simulation compiles | The compiled product is rebuilt from the new document | Rules keep their latches and bodies keep their poses |

The first five rows describe what the keys above imply. The same look values
set live (`world.row.set`) were watched in a windowed World and built no
program and began no bake; through a reload, and for the form and placement
rows, [the checks](#checks) below are owed.

### How a reload installs

- **Against the revision the authority took, merged over the live document.**
  The authority keeps the source revision it last took
  (`WorldServer.SourceRevision`). The sender reads the source as an edit,
  composed, drawn and settled but never admitted
  (`WorldDefinitionLoader.TryLoadFileForEdit`), answers a source that still
  composes to that revision's content pin as unchanged without parsing or
  sending it, and otherwise cuts the delta between the two revisions
  (`WorldReloadDelta.Between`). The authority reconstructs the next revision
  (`TryReconstruct`) and merges it over the live document (`TryMergeOver`). An
  ordinary keyed overlay visits the rows it changes, preserving the instances of
  unchanged live rows. The source wins whenever both sides changed the same row
  differently. The verdict names those rows and the members the reload changed.
- **Undo follows the accepted edit.** A local file reload appends the overlay
  that actually installed to the same journal as console mutations
  (`WorldJournalEntry.TryCompose`). An unchanged reload advances the source
  revision without adding an entry. A reload entry records the principal whose
  save submitted it. `world.undo` undoes the acting principal's own last edit
  and replays the World's own entries (its sweeps, searches, field writes and
  rule effects, and scoped replays), so the author's edit is undone however
  many of those followed it; `world.redo` takes it back in place. A replayed
  overlay reads again only the rows it names
  (`WorldDefinitionMembers.ApplyPatch`), and the World's entries ahead of the
  first edit fold into the base, so they cost an undo nothing
  (`WorldPersistence.FoldJournal`). An undo whose undone entries changed only
  state row values, or only creations in what shading reads and shade programs,
  sets those rows back on the live document and takes the install a reload of
  that change takes, validated once (`WorldPersistence.ToggledEdit`), keeping
  the session's state; any
  other undo installs the replayed document whole. Undo and reset leave the
  file on disk alone. The checkpoint keeps the source revision independently of the undo
  base so the next save merges against the source the authority last accepted.
- **Prepared clients receive changed rows.** `WorldOutputHub.DeliverPresentation`
  sends changed creations and shades. `WorldDeliveryCursor` advances the
  replica identity from shared row digests, and `WorldProjectionFeed` can
  send changed content references to presentation observers. Value installs
  leave the replica's document in place while `WorldDeliveryRows` delivers
  its changed values with a snapshot. An observer that loses disclosure
  receives a new primer.
- **Local source boots start the watch.** `WorldSourceWatch` starts from the
  file facts read at boot and submits an ordinary reload on a save.
  `world.counters world.boot` reports pump calls, stamp queries, content
  reads, delivered presentation rows, and the bytes a wire carried them in.
- **As the edit it is.** `WorldDocument.ReloadInstallKindOf` classifies the
  candidate by which parts of the document (`WorldDocumentInputs.Changed`) are other objects than the live document's:
  nothing installs when none is; state values alone take the value install;
  creations changed only in shading, and shade programs, take the presentation
  install; anything else takes the full install. Only the full install compiles
  rules, and `WorldServer.RecompileRules` reuses a compilation whose document
  differs from the installed one only in state values. The full install names
  the state rows the source changed (`WorldDocument.NameReloadEdit`) and
  installs as a mutation installs: the live arena itself when no row changed,
  otherwise a replacement born with the changed rows that carries every other
  row's live values (`WorldServer.TryPrepareArenaReplacement`), under
  `WorldArenaContinuity.Edit`, with lattices keeping their cells. A colour edit
  does not reset a game in progress, a lift, a suit or a noclip flight, and no
  reload deals placements again. `world.reset` and `world.load` install a
  document whole.
- **Validated once, off the tick.** The authority's preparation
  (`WorldRebuildPreparation`) validates as the kind needs: the touched rows for
  a value install, the document without compiling rules for a presentation
  install, an admission whose programs the install adopts for a full one.
  `ApplyRebuild` takes the preparation while the revision and the live document
  it was merged against still stand, and merges again on the tick otherwise.
- **Floor first for an edited group.** A planned group whose shape
  (`SdfMeshBakeGroup.Shape`: its geometry before its tier joins the hash) no
  held group has was made by an edit. It plans its floor and owes its demanded
  tier, as a group arriving with nothing to draw does. The mesh from before
  the edit draws until the floor lands, then the floor until the detail does.
  A group whose selector alone moved has a shape the bake held and takes its
  mesh from the library at once.
- **Timed where the time goes.** A reload prints the source's compile, its
  load, its text check and the delta (`[world.reload] source`), then the
  authority's reconstruction, validation and install (`[world.rebuild]`).
  The loop's own `world.iteration` line counts the source work beside its
  stages (compiles, compositions, root members composed, keyed rows merged on
  reload, documents read and
  validations), so a reading of seconds and a reading of work come from one
  loop.

Counted in a headless debug World (`world.counters world.boot`, the change in
each count across one `world.reload`):

| Reload | Validations | Rule compilations | Installs | Parses | Patch |
|---|---|---|---|---|---|
| Unchanged source, first reload after boot (the boot revision carries no content pin) | 0 | 0 | none | 1 | 2 bytes |
| Unchanged source, later | 0 | 0 | none (not sent) | 0 | none |
| One palette colour in `worlds/debug/auction.puck` | 1 | 0 | 1 presentation | 1 | 209 bytes |

### The invariant

> What a reload installs is the source's change between the revision the
> authority took and the next one, merged over the live document; what the
> install keeps is decided by which parts of that candidate are other
> objects than the live document's, and by nothing else.

- **Enforced by** one door. Every reload candidate reaches
  `WorldDocument.ApplyRebuild` through `WorldReloadDelta.TryMergeOver`: a live
  delta's preparation off the tick, and any other holder of the next revision
  (a replay drive's re-read, a supplied document, a preparation an install
  overtook) at the apply, by the same cut and merge. So an object's identity
  means "neither the source nor this reload changed this" on a live run and on
  its replay alike. The narrower installs are named by the members they may
  change (`WorldDocument.ReloadInstallKindOf`), so a change to any other member
  takes the full install. A recorded reload is a path and a content pin; the
  replay reads the file again and installs the same candidate.
- **Where it stops.** The tape still re-reads a file reload by its content
  pin. `WorldDocumentWalker.Share` keeps a list whole when its typed rows
  and its tree's rows do not line up, and derives the unshared values again.

### The door the authoring loop asks for

[The authoring loop](iteration/authoring-loop.md#an-edit-is-one-keyed-delta)
needs a door that takes a keyed delta with the revision it was made against.
That door is `WorldRebuildRequest` carrying a `WorldReloadDelta`: the patch and
the pins of both source revisions. File reload is an operator operation. A
published world's granted author needs section-scoped admission through the
[shared write door](iteration/authoring-loop.md#an-edit-is-one-keyed-delta).

A mutation's full install and a reload's full install already decide alike what
they rebuild: each compiled product records the parts of the document its build
read, and `WorldDocument.PlanProducts` rebuilds the products whose parts the
candidate changed ([which compiled products rebuild](../../src/Puck.World.Server/README.md#which-compiled-products-rebuild)).
The placements section's policy is a part of its own, so an authoring-defaults
edit rebuilds nothing that read only the rows, and a reload that changes
`bodyMotionPrograms` or the simulation rate rebuilds the population, which reads
both.

### Checks

| What | How to see it | Seen |
|---|---|---|
| A reload with no state change keeps the arena | The verb answers `state kept` | Headless |
| A reload after a live creation edit installs | `world.row.set creations <id> document.palette[0].color <json>`, then `world.reload` | Headless |
| A reload that changes one state row's values seeds that row alone | The verb answers `state rows re-seeded 1, state kept`, and a cell written in another row reads the same after | Owed |
| A colour edit bakes nothing | `host.log category sdf.mesh-bake` shows no group begun after the reload | Owed, windowed |
| A form edit bakes that creation's groups, floor first | The same log names only those groups, and `world.residency`'s floor line counts them before their detail lands | Owed, windowed |
| A recorded reload reproduces | `puck test --reproduce` over a scenario that reloads mid-run | Owed |
| An unchanged source is not sent | The verb answers `the source is unchanged since the revision the authority holds` | Headless |
| A one-colour reload is a presentation install | The verdict names `presentation install` and `changed prototypes`; `world.boot.rule-compilations` does not move | Headless |
| A live edit and a file edit to another row both stand | `world.row.set` one creation, edit another in the file, `world.reload`, read both with `world.row` | Headless |
| A row both changed takes the source's and is named | The verdict names `the source won over the session's edits at $.prototypes[id="…"]` | Headless |

### What is left

- **Composition and validation in front of the install.** The sender admits
  nothing, and a one-colour edit merges one root member. A changed member
  still merges its whole list, and the authority validates the whole candidate
  for every install but a state-value one (the authoring loop's steps 3 and 5).
- **The sender serializes both revisions once.** `WorldDefinitionMembers`
  slices a revision's compact bytes the first time a delta reads it; the next
  revision's slices come from one serialization of it. It goes when the compile
  session hands over the members it already knows (authoring loop step 3).
- **A material change relights the whole window.** The materials key
  (`SdfMeshIrradiance.MaterialsKey`) already reads only the materials the
  static scene's triangles carry, but a change to one of them moves the
  local lighting version. Sun and sky updates already compare per-probe inputs
  in `SdfMeshIrradiance.ChangeOf`. A material edit still needs an index of
  which probes hit that material, with later sweeps propagating the bounce.
- **A delivery emits the whole program on the frame thread.**
  `SdfCompositionFrameSource.CaptureFrame` runs every emitter's `Emit` on the
  frame that finds the content moved, and only the build after it runs on the
  pool. Each emitter reads live presentation state (the client's definition
  and mirror, the stamp pools, the adjacency band's scratch), so moving the
  emission needs an emitter contract that snapshots what it reads on the frame
  thread and emits from the snapshot. The presentation query field already
  works that way (`WorldFramePresenter.RebuildStaticField`).
