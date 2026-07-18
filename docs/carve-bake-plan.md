# Carve baking

Carve baking is the render-only cache that replaces a dense cluster of settled
sphere carves with one sampled distance-field region. The analytic scene and
carve list remain authoritative. Editing a baked region invalidates its brick,
returns the affected carves to analytic rendering, and schedules a replacement
bake.

For a conceptual explanation, read
[SDF handbook: Bricks and baking](sdf-handbook/09-bricks-and-baking.md). This
page records the implementation contract and maintenance points.

## Representation

A brick stores only the union of the subtraction volumes:

```text
c(p) = min(|p - center_i| - radius_i)
```

The SDF program composes that field with an ordinary hard subtraction. The
subject remains analytic on both sides of the brick boundary, which avoids a
stitch between analytic and sampled versions of the same surface.

The baker stores `c / √3`. Trilinear interpolation of the scaled samples is
1-Lipschitz, so `SampledRegion` does not change the program-wide `stepScale`.
Outside the brick box, the shader returns the box distance plus the baked
`boundaryFloor`; this is a positive lower bound that keeps hard subtraction
far-neutral.

## Runtime lifecycle

`SdfCarveBakePlanner` groups eligible hard sphere carves into spatial bins and
drives this state sequence through `ISdfBrickBakeService`:

1. Analytic carves remain visible while a bin is changing.
2. After the configured settle interval, the planner requests a brick bake.
3. `SdfWorldEngine` dispatches bounded slices of `sdf-brick-bake.comp.hlsl`
   until the brick is ready.
4. A content revision rebuild replaces the bin's analytic instances with one
   `SdfProgramBuilder.SampledRegion` instance.
5. An edit that intersects the bin invalidates the brick immediately and
   restores analytic emission.

Pool capacity freezes when `SdfWorldEngine` is constructed. A zero
`BrickPoolVoxelCapacity` disables brick allocation (a 1-float filler keeps the
always-present `sdfBrickPool` binding valid). Baking and rendering are split: a
pool-less engine still ACCEPTS a program containing `SampledRegion` and renders
it via the shader's conservative uncarved-hull fallback (`sdfSampledRegion`
detects the filler by element count and returns `SDF_FAR_DISTANCE`, so the
Subtraction never bites). Only `RequestBrickBake` fails explicitly on a pool-less
engine — there is nothing to bake into. This is what lets every offscreen
filming view (`SdfCameraView`/`NestedWorldView`) run with capacity 0 (no dead
64 MB pool) while still rendering a filmed `SampledRegion` world uncarved.

## Contract boundaries

- Authoring always targets the analytic carve list, never the sampled brick.
- Bricks are session-transient GPU cache data; do not serialize them into run
  documents, creations, replays, or content-addressed storage.
- Simulation and `IWorldQuery` never read brick data.
- The brick contains only hard sphere-subtraction unions. Smooth or chamfered
  subtraction remains analytic because sequential smooth composition cannot be
  represented by this union cache.
- The planner owns slot allocation, settling, invalidation, and eviction.
  `SdfWorldEngine` owns device memory and bake execution.

## Synchronization points

Keep these components synchronized when changing the format or lifecycle:

| Host | Shader | Contract |
|---|---|---|
| `SdfProgramBuilder.SampledRegion`, `SdfShapeType.SampledRegion` | `sdfSampledRegion`, `SDF_SHAPE_SAMPLED_REGION` | packed lanes, dimensions, bounds, fallback |
| `SdfBrickBakeRequest`, `SdfBrickPoolLayout` | `sdf-brick-bake.comp.hlsl` | pool offsets, voxel centers, stored distance scale |
| `SdfWorldEngine.RequestBrickBake` and bake-state polling | bake pipeline and barriers | sliced dispatch and `Empty → Baking → Ready` visibility |
| `SdfCarveBakePlanner` | program rebuild through the frame source | analytic-to-brick handoff and invalidation |

The views and beam kernels bind the brick pool. Kernels without the pool use
the conservative uncarved fallback rather than risking a hole.

## Verification

Engine changes use the Puck Post battery described by the
`verifying-puck-changes` skill. Also run the SDF demo and compare analytic and
baked carve workloads with the same camera and shading settings. Verify both
the steady baked state and an edit that forces immediate invalidation.
