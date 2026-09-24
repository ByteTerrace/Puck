# SDF handbook

This handbook explains how Puck's signed-distance-field world becomes a
rendered frame, how scene programs are authored, and how rendering stays
separate from deterministic simulation queries. It is written as a guided
course, but each chapter can also be revisited on its own.

## Reading order

| Chapter | What it teaches |
|---|---|
| [Signed distance fields](signed-distance-fields.md) | Distance fields, composition, sphere tracing, and scene data. |
| [SDF program model](program-model.md) | The flat instruction stream, accumulator, scopes, materials, instances, and Lipschitz step bounds. |
| [SDF frame rendering](frame-rendering.md) | Frame assembly, culling, primary marching, shading dispatches, render scale, and frame rings. |
| [Lighting and shading](lighting-and-shading.md) | Normals, ambient occlusion, shadows, materials, and screen surfaces. |
| [Authoring SDF scenes](authoring-scenes.md) | `SdfProgramBuilder`, coordinate spaces, composition, capacity checks, and authoring pitfalls. |
| [Motion and views](motion-and-views.md) | Presentation anchors, camera rigs, `ViewStack`, transitions, and screen views. |
| [Queries and determinism](queries-and-determinism.md) | `IWorldQuery`, exact fixed-point evaluation, probes, raycasts, and derived gravity. |
| [SDF performance](performance.md) | Live cost measurement, occupancy, culling, and render-scale choices. |
| [Bricks and baking](bricks-and-baking.md) | Sampled distance bricks, march safety, and cache lifecycle; prototype meshes, textures, and impostors baked from the field. |

If you're focused on rendering, read [SDF frame rendering](frame-rendering.md),
then [Lighting and shading](lighting-and-shading.md), then
[SDF performance](performance.md). If you're authoring content, read
[Signed distance fields](signed-distance-fields.md), the
[SDF program model](program-model.md), [Authoring SDF scenes](authoring-scenes.md),
and [Motion and views](motion-and-views.md). For simulation and physics
questions, go to [Queries and determinism](queries-and-determinism.md).
[Bricks and baking](bricks-and-baking.md) assumes you've read the program model
and frame rendering chapters.

## Related references

The [SDF technical reference](../reference/README.md) contains focused articles
on acceleration, field correctness, gradients, filtering, materials, text,
shading, and recorded technique decisions. The [`Puck.SdfVm` README](../../../../src/Puck.SdfVm/README.md)
documents the runtime and host-facing render assembly. Shared shader manifests
and connected pipelines are documented in the [`Puck.Shaders` README](../../../../src/Puck.Shaders/README.md).

The [rendering programme](../../../plans/rendering.md)
describes planned work beyond the current renderer. It is separate from this
handbook's description of implemented behavior.
