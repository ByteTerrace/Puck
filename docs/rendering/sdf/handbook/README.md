# SDF handbook

This handbook explains how Puck's signed-distance-field world becomes a
rendered frame, how scene programs are authored, and how rendering stays
separate from deterministic simulation queries. It is written as a guided
course, but each chapter can also be revisited on its own.

## Reading order

| Chapter | What it teaches |
|---|---|
| [1. The idea](01-the-idea.md) | Distance fields, composition, sphere tracing, and scene data. |
| [2. The program model](02-the-program-model.md) | The flat instruction stream, accumulator, scopes, materials, instances, and Lipschitz step bounds. |
| [3. The frame](03-the-frame.md) | Frame assembly, culling, primary marching, shading dispatches, render scale, and frame rings. |
| [4. Lighting and shading](04-lighting-and-shading.md) | Normals, ambient occlusion, shadows, materials, and screen surfaces. |
| [5. Authoring](05-authoring.md) | `SdfProgramBuilder`, coordinate spaces, composition, capacity checks, and authoring pitfalls. |
| [6. Motion and views](06-motion-and-views.md) | Presentation anchors, camera rigs, `ViewStack`, transitions, and screen views. |
| [7. Queries and determinism](07-queries-and-determinism.md) | `IWorldQuery`, exact fixed-point evaluation, probes, raycasts, and derived gravity. |
| [8. Performance](08-performance.md) | Cost measurement, occupancy, culling, render-scale choices, and benchmark limits. |
| [9. Bricks and baking](09-bricks-and-baking.md) | Sampled distance bricks, march safety, and cache lifecycle. |

Readers focused on rendering can follow chapters 3 → 4 → 8. Readers authoring
content can follow 1 → 2 → 5 → 6. Chapter 7 is the route for simulation and
physics questions; chapter 9 assumes the program and frame models from 2 and 3.

## Related references

The [SDF technical reference](../reference/README.md) contains focused articles
on acceleration, field correctness, gradients, filtering, materials, text,
shading, and recorded technique decisions. The [`Puck.SdfVm` README](../../../../src/Puck.SdfVm/README.md)
documents the runtime and host-facing render assembly. Shared shader manifests
and connected pipelines are documented in the [`Puck.Shaders` README](../../../../src/Puck.Shaders/README.md).

The [shader pipeline evolution plan](../../../plans/shader-pipeline-evolution.md)
describes planned work beyond the current renderer. It is separate from this
handbook's description of implemented behavior.
