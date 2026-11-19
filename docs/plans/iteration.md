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
| Engine code | A C# or HLSL edit | Shaders reload live; a restart reuses every baked mesh and settled light the edit did not invalidate |
| A cold machine | An empty store | About a minute per backend to bake the debug world's meshes, lighting settled soon after |

## How each is measured

Every loop is timed from the edit to the first presented frame that shows it
and to the settled frame (meshes landed, lighting converged), by the World
itself, so a lane reads the loop it changed and the batch reads every loop.
Building that instrument is the first step.

## What makes the loops slow today

- **A code edit cold-bakes the world.** The bake's identity covers several
  hundred code members, so almost any engine edit misses every stored mesh.
- **Lighting settles in minutes.** The irradiance scene is rebuilt as detail
  tiers land and a retier relights the whole window; a first boot's main volume
  settled after nearly ten minutes.
- **A cold bake is latency-bound.** The bake kernel is about 2.4% of a cold
  bake's wall time, and the GPU is idle most of the time a job is in flight
  ([the measurement](field-programs/research-measure.md)). Residencies bake the
  same groups twice.
- **An author's `.puck` edit takes the slow door.** A saved file reaches the
  running World only as a `world.reload` (by hand, or queued by `world.watch
  on`), which composes and validates the whole world again for one changed
  value before [the live edit path](#the-live-edit-path-step-3) installs it,
  while the same value set through `world.row.set` lands in a fraction of a
  second. [The authoring loop](iteration/authoring-loop.md)
  maps that path through the compiler, the caches, the CLI, the World, the
  editor and the save back to source, and designs the loop that replaces it.

Two diagnosis lanes (`bake-trace`, `light-trace`) account for the bake's and
lighting's time, and one fix lane (`bake-share`) removes the duplicate bakes.

## Order of work

1. The instrument: every loop timed in the World and readable by a verb.
2. Keys by stage: each bake stage keyed on what it reads, so an engine edit
   outside the field and its kernel reuses every stored mesh.
3. The live edit path: a saved `.puck` reaches the running World as the
   smallest change it is, with look values as appearance uploads and form
   edits as a progressive rebake of only the creations touched.
4. Lighting from a stable proxy, relit incrementally, settled at boot with the
   whole GPU.
5. The cold bake fed: in-flight work shared, groups in flight bounded by the
   GPU rather than round trips, a load mode that gives boot the whole GPU.

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
| Describe a document change by key, and prove it | `WorldDocumentBasis.Diff` and `WorldDocumentBasis.TryMerge`, carried as a `WorldReloadDelta` |
| Keep what a change did not touch | `WorldDocumentWalker.Share`: the new document holds the old one's own object for every member and keyed row the patch leaves unchanged |
| Install a small change without rebuilding the simulation | `WorldMutation` through `WorldDocument.InstallKindOf` |
| Emit again only the placements that changed | `WorldStaticPalettes.Emissions`, keyed by the placement row and the creation |
| Upload only the program words that changed | `SdfProgramDelta` |
| Bake again only the groups whose form changed | `SdfMeshGeometry`, the key `SdfMeshPathBake` keeps a group by, and `SdfMeshLibrary` |
| Show something before the detail lands | A group's floor (`SdfMeshPathBake`, `SdfMeshBakeGroup.Shape`) |
| Relight only what changed | `SdfMeshIrradiance`: its static scene key for geometry, its materials key for what the scene's materials reflect |
| Run only the shading programs a frame names | The `shades` table and specialization by program set ([authored shading](../../.claude/skills/rendering/references/authored-shading.md)) |

### The diff, and why no table lists consequences

The old compiled world and the new one are compared once, by content, as
documents. `WorldDocumentBasis.Diff` over their canonical trees gives a keyed
patch, and `WorldDocumentWalker.Share` rebuilds the new document so that
everything the patch does not name is the old document's own object. After
that, whether a row or a member changed is an identity test.

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

The first five rows describe what the keys above imply. They have not been
watched in a windowed World: the lead machine had no memory headroom for one
while this step was built, so [the checks](#checks) below are owed.

### How a reload installs

- **As the edit it is.** `WorldDocument.ApplyRebuild` names the state rows the
  source changed (`WorldDocument.NameReloadEdit`: a row is unchanged when it
  is the installed object or serializes to the same bytes) and installs a
  reload as a mutation installs: the live
  arena itself when no row changed, otherwise a replacement born with the
  changed rows that carries every other row's live values
  (`WorldServer.TryPrepareArenaReplacement`), under
  `WorldArenaContinuity.Edit`, with lattices keeping their cells. A colour
  edit does not reset a game in progress, a lift, a suit or a noclip flight.
  `world.reset` and `world.load` install a document whole.
- **Without reconstructing in process.** `WorldReloadDelta.Create` holds both
  documents, so it shares the candidate itself and keeps it beside the
  declarations it diffed; `WorldReloadDelta.TryPrepare` over that same object
  answers it. The wire and a remote authority carry the patch and reconstruct.
- **Floor first for an edited group.** A planned group whose shape
  (`SdfMeshBakeGroup.Shape`: its geometry before its tier joins the hash) no
  held group has was made by an edit. It plans its floor and owes its demanded
  tier, as a group arriving with nothing to draw does. The mesh from before
  the edit draws until the floor lands, then the floor until the detail does.
  A group whose selector alone moved has a shape the bake held and takes its
  mesh from the library at once.
- **Timed where the time goes.** A reload prints the source's compile, its
  composition and admission, its text check and the diff
  (`[world.reload] source`), then the authority's reconstruction, admission
  and install (`[world.rebuild]`). Composition and admission are most of the
  wait and the diff is most of the rest; the install is a small share. These
  lines move into the loop clock when it lands.
- **A refused delta says where.** A patch whose merge is not the target names
  the first path that differs. `WorldDocumentBasis.Diff` replaces an object
  that gains an explicit null, which a member-wise delta cannot express, so a
  reload after a live creation edit installs.

### The invariant

> What an install keeps is decided by the content diff between the installed
> declarations and the candidate, and by nothing else.

- **Enforced by** one door. Every reload candidate reaches
  `WorldDocument.ApplyRebuild` shared with the installed declarations through
  `WorldReloadDelta`: a live delta by `TryPrepare`, and any other holder of
  the whole target (a replay drive's re-read, a delta an install overtook) by
  `WorldReloadDelta.Share` at the top of the apply. Both share by the same
  patch, so an object's identity means "the source did not change this" on a
  live run and on its replay alike. What the simulation keeps rests on
  content directly: `WorldDocument.NameReloadEdit` compares each state row
  with the installed one, so the rows a reload seeds do not depend on what
  was shared. A recorded reload is a path and a content pin, the replay reads
  the file again, and it seeds the same rows.
- **Where it stops.** A product that reads more of the document than the rows
  it is kept under would stand stale; the keys in the table above are what
  this path trusts. A draw row a document fills at install is filled again by
  a reload. Sharing is an economy, never a proof: `WorldDocumentWalker.Share`
  keeps a list whole when its typed rows and its tree's rows do not line up,
  as the state rows do not once one of them changes, and what was not shared
  is derived again.

### The door the authoring loop asks for

[The authoring loop](iteration/authoring-loop.md#an-edit-is-one-keyed-delta)
needs a door that takes a keyed delta with the revision it was made against.
That door is `WorldRebuildRequest` carrying a `WorldReloadDelta`: the patch,
the authority document version it was diffed against, and both fingerprints.
It reuses `WorldDocumentBasis.Diff`, `TryMerge` and `WorldDocumentWalker.Share`
and installs as an edit. It differs from that memo's target in three ways:

| The door | The target | Owned by |
|---|---|---|
| The patch is diffed against the installed declarations, so it also undoes what the session changed live | A patch between two source revisions that merges over the live document and refuses a row both changed | Authoring loop steps 3 and 4 |
| The install clears the journal and moves the base, and only the operator may send it | One journal entry, admitted by the sender's grants for the sections the patch names | Authoring loop step 4 |
| The simulation's products are rebuilt from the new document on every reload; what they keep (state, latches, poses, lattice cells) they keep by continuity | A patch that reaches nothing the simulation reads is a presentation install | The next step on this door, below |

On the third row this door should lead. `WorldDocument.InstallKindOf` decides
by mutation kind from lists (`AffectsPopulation`, `AffectsSolidField`), and a
keyed patch has no kind. The rule both need is the one the client side runs
on: each compiled product names the document members it is built from, as the
parameters of the function that builds it, and is kept while those are the
same objects. A mutation and a patch are then judged alike, a placement that
is neither solid nor inhabited stops rebuilding the population, and the lists
go. It is a refactor of the server's install. Its check is `world.counters
world.boot` after a one-colour reload reading no rule compile and no
population rebuild.

### Checks

| What | How to see it | Seen |
|---|---|---|
| A reload with no state change keeps the arena | The verb answers `state kept` | Headless |
| An authority in the sender's process reconstructs nothing | `[world.rebuild]` prints `reconstruct=0 ms` | Headless |
| A reload after a live creation edit installs | `world.row.set creations <id> document.palette[0].color <json>`, then `world.reload` | Headless |
| A reload that changes one state row seeds that row alone | The verb answers `state rows re-seeded 1 kept N`, and a cell written in another row reads the same after | Headless |
| A colour edit bakes nothing | `host.log category sdf.mesh-bake` shows no group begun after the reload | Owed, windowed |
| A form edit bakes that creation's groups, floor first | The same log names only those groups, and `world.residency`'s floor line counts them before their detail lands | Owed, windowed |
| A recorded reload reproduces | `puck test --reproduce` over a scenario that reloads mid-run | Owed |

### What is left

- **Composition and admission in front of the diff** are most of a reload's
  wait: the authoring loop's steps 1 to 5.
- **The diff serializes both documents.** It goes when the compile session
  hands over the patch it already knows (authoring loop step 3).
- **A material change relights the whole window.** The materials key is one
  key for every material the scene holds. A relight costs far less than a
  trace and cycles under a moving sun in any case; narrowing it to the probes
  whose hits carry the changed material belongs to step 4 of this plan.
- **A reload deals placements again.** Its document holds no dealt placement,
  so the deal sweep adds them back and the client builds its program a second
  time. It stops when a patch merges over the live document.
- **The watch is off until asked.** `world.watch on` starts it. Turning it on
  at boot for a local operator's source tree is the authoring loop's step 4;
  its poll runs on the host pump and wants a reading of its cost in a frame
  first.
