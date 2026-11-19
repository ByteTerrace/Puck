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

The World times every loop itself (`WorldIterationClock`, read with
`world.iteration`; [the reference](../reference/commands.md#edit-loops)). Each
loop is timed from the edit to the first produced frame that finds each of
four stages true:

- `frame`: the frame renders the edited document, or a reloaded kernel, with
  the program its content calls for. A look value shows here.
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
carries the commands and the World's own lines. Later lanes improve on these
figures and replace the table with their own readings.

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
| Cold boot, empty cache | fresh `--cache-dir` | not measured | | | |

What the baseline shows:

- **`lit` is never reached on the debug world.** Its day clock moves the sun
  every frame, and the irradiance volume's relight falls behind it: it relit
  about 1,500 to 1,900 probes a second where its age bound needs about 2,500
  (`[sdf.mesh-gi] behind level=0 … needed-per-second=2496`), over ten minutes
  of watching. Lighting from a stable proxy, relit incrementally (step 3), is
  the fix this figure waits on.
- **Every look value costs a program build and about a second.** An exposure
  or light edit, read live off the definition each frame, still rebuilt the
  composition's program (`builds=1`) before a frame showed it.
- **A palette colour rebakes meshes.** The live colour edit restarted the bake
  with 15 groups baking and one loading
  (`[sdf.mesh-bake] start … baking=15 loading=1 … cause=program`), so the
  colour took 21 seconds to show whole: colour is in the bake's input, not an
  appearance upload (step 2).
- **A saved edit spends most of its time before the first frame.** Each
  `world.reload` took 9 to 15 seconds to reach a frame, though the rebake after
  it took 0 to 3: compiling, admitting, diffing and installing the document is
  the live edit path's first cost (step 2).
- **A kernel reload shows in 3 seconds and finishes in 49.** The first changed
  pipeline installs quickly; the request stays pending until every carried
  source in the tree has compiled.
- **A restart spends 47 seconds before its first frame** even with every mesh
  in the store (`loaded=2`, no dispatches), and the World's own build after
  the engine edit ran for more than two minutes before that (the store build
  was still running at 124 seconds).
- **After a live row edit, `world.reload` refuses every later reload** with
  `world.reload.delta-unrepresentable` and no reason, even once the edited
  values are restored, so live tweaking and saved edits cannot be mixed in one
  session.

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
- **An author's `.puck` edit has no live path** from a saved file to the
  running World that is measured and fast.

Two diagnosis lanes (`bake-trace`, `light-trace`) account for the bake's and
lighting's time, and one fix lane (`bake-share`) removes the duplicate bakes.

## Order of work

1. Keys by stage: each bake stage keyed on what it reads, so an engine edit
   outside the field and its kernel reuses every stored mesh.
2. The live edit path: a saved `.puck` reaches the running World as the
   smallest change it is, with look values as appearance uploads and form
   edits as a progressive rebake of only the creations touched.
3. Lighting from a stable proxy, relit incrementally, settled at boot with the
   whole GPU.
4. The cold bake fed: in-flight work shared, groups in flight bounded by the
   GPU rather than round trips, a load mode that gives boot the whole GPU.

The missing look tools, authored large terrain form and a stylized surface
detail layer, are built alongside, each judged through these loops.
