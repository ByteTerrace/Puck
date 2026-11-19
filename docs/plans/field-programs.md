# Field programs

The frame no longer interprets SDF programs
([the decision](../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)),
but the bake, CPU queries, contact and the editor still do, through two
interpreters: the CPU evaluator and the GPU kernels' `mapCore`. This plan asks
what that still costs and what to build instead. The owner authorized the
design round.

## What it costs today

- **Bake time.** The bake kernel walks the program for every sample
  (`sdf-mesh-bake.comp.hlsl`). Cold debug-world bakes take minutes against the
  iteration loop's target of about a minute per backend, and a creation whose
  relief, bends or bumps bound its distance rather than give it exactly takes
  more refinement (the glacier's one scope-wide clamp, `stepScale 0.159`).
  [The measurement](field-programs/research-measure.md) puts the bake kernel
  at a few seconds of GPU time in a cold bake of minutes, splits a heavy
  program's per-sample cost between walking and arithmetic, and prices the
  clamp.
- **Two readers and many walkers.** Every op is interpreted on the CPU and on
  the GPU, and the CPU side switches over it in roughly ten analyses (point
  walk, bounds, lane contract, cells, Lipschitz, cull, skip spheres, rigid
  plan, distance gain, certificates). Readers that disagree are a recurring
  defect: the superellipsoid and capsule disagreed between CPU and GPU, and the
  relief mask needed a do-nothing arm in every walker and a review to find the
  ones it missed. The C# and HLSL pair is a review-only hazard for this reason.
- **What the bake drops.** Per-pixel material blends at smooth seams, partial
  silhouette coverage and curvature finer than the bake's cell. This is the
  mesh-first trade more than the interpreter, and is recorded here because a
  better field pipeline may recover some of it.

## The round

| Lane | Question |
|---|---|
| `field-measure` | On a cold bake of the debug world, what share of the bake kernel's GPU time is program walking and what share is sample count? What does one hand-specialized kernel for a heavy creation gain? |
| `field-ops` | How can one definition per op generate the CPU readers, the GPU arms and every analysis walker's arm, so no op can exist in one reader and not another? |
| `field-elsewhere` | How do other signed-distance engines and tools specialize, compile or cache their programs, and what do their choices cost and buy? |

## Design

To be written by the synthesis.
