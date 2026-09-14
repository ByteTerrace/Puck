# Rendering

Puck's rendering documentation explains how authored world data becomes a
displayed frame. It covers the shared shader-pipeline system and the current
signed-distance-field renderer. Rendering is a presentation concern; the
deterministic simulation and query contracts remain documented with their
owning projects.

## Start here

- [SDF renderer](sdf/README.md) introduces the implemented signed-distance
  field renderer and routes to its handbook and technical reference.

- [`Puck.Shaders` README](../../src/Puck.Shaders/README.md) documents shader
  manifests, pipeline graphs, resource bindings, configuration, and live
  loading.

- [Shader pipelines and hybrid rendering](../plans/shader-pipeline-evolution.md) records
  the planned work beyond the current pipeline foundation, including hybrid
  rendering and shared visibility.

## How the pieces fit

The SDF renderer evaluates a packed scene program while its GPU passes generate
and shade the view. The shared shader system supplies the manifest and pipeline
contracts used by additional compute and fullscreen stages. A host chooses and
assembles these presentation stages; it does not change the integer-clock
simulation state or the exact query providers.

The [SDF handbook](sdf/handbook/README.md) is the guided explanation. The
[SDF reference](sdf/reference/README.md) holds detailed algorithm, correctness,
shading, and measured-technique material.
