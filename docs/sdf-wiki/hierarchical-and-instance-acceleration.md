# Hierarchical and instance acceleration

The renderer accelerates analytic instances in two stages: a conservative beam
prepass identifies candidate instances for a screen region, and a world-space
uniform grid limits the instances considered by that prepass. The primary
march then evaluates the packed instance mask for its tile.

## Current structure

`SdfProgram` packs finite instance bounds and the uniform-grid metadata. The
beam shader traverses the grid, tests candidate bounds, and writes per-tile
masks. Programs that cannot provide a sound finite bound remain visible to the
flat fallback path.

The grid and flat paths must produce the same image. `buildInstanceGrid: false`
exists as the reference configuration for this comparison.

## Bound requirements

An instance bound must include every operation that can move or expand its
surface:

- authored transforms and repeated copies;
- smooth-composition halos;
- scoped `Onion`, `Dilate`, and `Displace` reach;
- sampled-region extents; and
- any material or shape behavior that changes visibility.

An undersized bound creates missing tiles and march holes. An oversized bound
is safe but reduces acceleration.

## When to consider another hierarchy

A BVH, TLAS/BLAS split, or work-graph traversal is justified only by a measured
workload the uniform grid cannot serve, such as severe density skew or very
large sparse worlds. A replacement must preserve:

- deterministic packing and traversal inputs;
- identical conservative visibility on Vulkan and Direct3D 12;
- a simple flat reference path; and
- bounded rebuild cost for live authoring.

Do not introduce a second hierarchy solely to reduce theoretical complexity.
Measure candidate count, prepass time, mask density, and rebuild time first.
