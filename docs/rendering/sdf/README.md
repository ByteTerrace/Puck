# SDF renderer

Puck's signed-distance-field (SDF) renderer represents scene content as a
packed program of distance-field operations. The GPU interprets that program,
marches rays through the resulting fields, and shades the visible surfaces.
The same scene data also has deterministic query and evaluation paths; those
paths are described where the handbook discusses queries and determinism.

This page is the entry point for the SDF documentation. The handbook teaches
the model in a guided sequence, while the reference section holds focused
technical articles and measured decisions.

## Choose a path

Read the [handbook](handbook/README.md) for a complete introduction. Chapters
1–2 establish fields and the packed program model. Chapters 3–4 explain frame
assembly and shading; chapters 5–6 cover authoring and views; chapter 7
separates deterministic queries from presentation; and chapters 8–9 cover
performance and sampled distance caches.

Use the [technical reference](reference/README.md) when you need a specific
contract or technique. It links to articles on marching and culling,
Lipschitz bounds, gradients, filtering, materials, text, shading, and recorded
verdicts.

## Renderer boundaries

The renderer and the deterministic evaluator share the packed instruction
layout and the field semantics. A GPU result is a presentation result: GPU
floating-point execution and backend-specific output are not the simulation's
authoritative state. Conservative bounds still govern ray marching and culling,
and the authored instruction stream remains the source representation while
sampled distance bricks serve as invalidatable render caches.

For the shared shader manifests, pipeline graphs, descriptor bindings, and
configuration model, see the [`Puck.Shaders` README](../../../src/Puck.Shaders/README.md).
For planned work beyond the current renderer, see [shader pipeline evolution](../../plans/shader-pipeline-evolution.md).

## Source-level references

- [`Puck.SdfVm`](../../../src/Puck.SdfVm/README.md) documents the SDF runtime,
  render assembly, captures, and host-facing APIs.
- [`Puck.Shaders`](../../../src/Puck.Shaders/README.md) documents shared shader
  sets and connected pipelines.
- The [handbook](handbook/README.md) and [reference](reference/README.md) are
  the detailed human documentation for this renderer.
