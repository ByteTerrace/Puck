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
`world.watch`) and `world.shaders.reload` begin loops themselves, and a script
names any other edit with `world.iteration begin <name>` on the line before
it. A settled loop prints `[world.iteration] …` on stderr, so a lane reads the
loop it changed and the batch reads every loop from the World's log.
`world.wait iteration <seconds>` holds a script until the latest loop
settles.

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

- **`lit` is never reached on the debug world.** Its day clock moves the sun
  every frame, and the irradiance volume's relight fell behind it: it relit
  about 1,500 to 1,900 probes a second where its age bound needs about 2,500
  (`[sdf.mesh-gi] behind level=0 … needed-per-second=2496`), over ten minutes
  of watching.
- **A saved edit spent most of its time before the first frame.** Each
  `world.reload` took 9 to 15 seconds to reach a frame, though the rebake after
  it took 0 to 3. Compiling, composing, diffing and installing the document was
  the cost; the counts above say how much of it is gone.
- **A kernel reload shows in 3 seconds and finishes in 49.** The first changed
  pipeline installs quickly; the request stays pending until every carried
  source in the tree has compiled.
- **A restart spends 47 seconds before its first frame** even with every mesh
  in the store (`loaded=2`, no dispatches), and the World's own build after
  the engine edit ran for more than two minutes before that (the store build
  was still running at 124 seconds). Nothing yet says where the boot's wait
  goes.

## What makes the loops slow today

AGENTS.md sets two figures as forcing functions: about half a minute from an
engine C# or HLSL edit to seeing it in the running debug world, and about a
minute to bake the debug world's meshes from an empty store, per backend. The
latest readings against them:

| Loop | Latest reading | Against the figure |
|---|---|---|
| HLSL kernel edit, `world.shaders.reload` | 3.2 s to the first frame that draws the reloaded kernel (the baseline) | Within it |
| C# edit that hot reload applies | Well under a second after the watch's first edit ([edit to running](../development/contributing.md#edit-to-running)); the loop clock does not time it | Within it |
| C# edit that needs a restart | The World's build, still running at 124 s, then 47 s to the first frame (the baseline) | Several times over |
| Cold bake, empty store | 114 s on Direct3D 12 and 123 s on Vulkan, free-running, on a shared machine | About twice over |

- **A restart is the C# loop's cost.** An edit hot reload cannot apply (a rude
  edit, or one in a project the watch does not list) rebuilds the World and
  boots it. The bake no longer adds to that for most edits, but the build and
  the boot to the first frame together take minutes, and nothing attributes
  the boot's wait. The [edit-to-running packet](open-items.md#edit-to-running-hot-reload-and-boot)
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
  proportion to the edit, but a process's first recomposition after it starts
  merges whole, every install but a state-value one validates the whole
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
   are paced by trace progress; one GPU budget is shared across residencies by
   their share of the display; and a seen volume's first light takes the
   allowance an arriving bake takes
   ([the settle research's fixes](illumination/research-settle-time.md#the-ranked-fixes)).
   Left: the reading that says whether `lit` is reached under the day clock; a
   session that renders the boot world sharing the world's meshes instead of
   holding its own copy (that research's fix 3, a streaming change); uploading
   only the top level of a scene change (its fix 9); and a relight driven by
   which probes' inputs moved, which a material edit needs. *Check:* the boot
   loop's `world.iteration` line reports `lit` on the debug world.
5. **The cold bake fed.** Built: an arriving group's bake work takes its own
   allowance of the GPU the frame leaves idle, the bake starts once its own two
   kernels compile, only groups arriving in view plan a coarse first tier, the
   contour and crossing stages rent their scratch, and an in-flight bake is
   shared across residencies. In flight: the bake's critical path from the
   sample checkpoint probe to the store write (`bake-latency`), its remaining
   garbage (`bake-garbage`) and its repeated CPU work (`bake-cpu`). After
   them, the quiet readings and the shared cross-backend store, in
   [bake proportional to change](open-items.md#bake-proportional-to-change).
   *Check:* a free-running cold boot's `[sdf.mesh-bake] done` line on each
   backend, read in the lead's quiet window, against the minute figure.

The missing look tools, authored large terrain form and a stylized surface
detail layer, are built alongside, each judged through these loops.

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
  (`TryReconstruct`) and merges it over the live document (`TryMergeOver`): a
  member the session has not changed takes the source's, and any other merges
  three ways, the source winning each row both changed. The verdict names those
  rows and the members the reload changed.
- **Undo follows the accepted edit.** A local file reload appends the overlay
  that actually installed to the same journal as console mutations
  (`WorldJournalEntry.TryCompose`). An unchanged reload advances the source
  revision without adding an entry. An undo whose dropped entries changed only
  creations in what shading reads and shade programs sets those rows back on
  the live document and takes the presentation install a reload takes
  (`WorldPersistence.DroppedPresentation`), keeping the session's state; any
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
  reads, and delivered presentation rows and bytes.
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
  stages (compiles, compositions, the root members merged, documents read and
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
  nothing, and a one-colour edit merges one root member, but a process's first
  recomposition of a persisted image merges whole, a changed member merges its
  whole list, and the authority validates the whole candidate for every install
  but a state-value one: the authoring loop's steps 3 and 5.
- **The sender serializes both revisions once.** `WorldDefinitionMembers`
  slices a revision's compact bytes the first time a delta reads it; the next
  revision's slices come from one serialization of it. It goes when the compile
  session hands over the members it already knows (authoring loop step 3).
- **A material change relights the whole window.** The materials key
  (`SdfMeshIrradiance.MaterialsKey`) already reads only the materials the
  static scene's triangles carry, but a change to one of them moves the
  lighting version, and every relight sweep lists every probe not relit in the
  current sweep (`SdfMeshIrradiance.List`, and the GPU selection beside it)
  until no probe moves past its bound. Narrowing the first sweep to the probes
  whose hits carry the material would change nothing while later sweeps relight
  the window for the bounce; it needs a relight driven by which probes' inputs
  moved, which is step 4.
- **A delivery emits the whole program on the frame thread.**
  `SdfCompositionFrameSource.CaptureFrame` runs every emitter's `Emit` on the
  frame that finds the content moved, and only the build after it runs on the
  pool. Each emitter reads live presentation state (the client's definition
  and mirror, the stamp pools, the adjacency band's scratch), so moving the
  emission needs an emitter contract that snapshots what it reads on the frame
  thread and emits from the snapshot. The presentation query field already
  works that way (`WorldFramePresenter.RebuildStaticField`).
