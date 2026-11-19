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
- **An author's `.puck` edit has no live path** from a saved file to the
  running World that is measured and fast.

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
