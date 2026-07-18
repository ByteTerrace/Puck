# SDF world render assembly

`SdfWorldRenderSpec` and `SdfWorldRenderBuilder` are the shared assembly path
for SDF world graphs. They translate backend-independent scene data into an
`SdfEngineNode`, its child viewports, screen sources, kernel set, capacity
envelope, and optional frame-source decorator.

## Boundary

The render builder owns rendering policy:

- backend selection through `SdfWorldRenderSpec.HostsOnDirectX`;
- SPIR-V or DXIL kernel resolution through `SdfWorldKernels`;
- child viewport-node creation from `ViewportSources`;
- screen-source providers from `ScreenSources`;
- program, instance, dynamic-transform, and viewport capacity envelopes;
- optional frame-source decoration through `DecorateFrameSource`;
- capture and per-pass timing configuration.

It does not own simulation, command routing, device hosting, or demo-specific
UI. A caller may install demo behavior through `DecorateFrameSource`, but
`Puck.SdfVm` must not reference demo types.

## Capacity envelope

GPU buffers freeze at engine construction. A frame source that can rebuild to
a larger program must probe or otherwise calculate its maximum envelope before
the builder creates the engine:

- `ProgramWordCapacity`
- `InstanceCapacity`
- `DynamicTransformCapacity`
- `ViewportCapacity`

The live program and frame may use less than the envelope. Exceeding a frozen
capacity fails explicitly; the renderer must not truncate program words,
instances, transforms, or views.

Every optional emission branch must have an equivalent capacity-probe branch.
When adding content that can appear after startup, update the probe in the same
change.

## Content seams

A viewport child and a diegetic screen source are different mechanisms:

- A child occupies a compositor viewport slot. The SDF beam and views passes
  skip that slot and the compositor copies the child's image.
- A screen source binds an image to a program-declared `ScreenSlab`. The slab
  remains SDF geometry and samples the image during shading.

`ScreenSourceDocument` maps a screen index to a provider. A provider that
returns zero unbinds the image for that frame and the slab uses its procedural
screen material. Dynamic screens also publish their world-space sampling frame
through `ISdfFrameSource.ScreenSurfaceTransforms`.

## Unsupported graph requests

`GraphBuilder.UnsupportedReason` is the single source of truth for world-graph
features the shared builder cannot host. Validate these requests before the
window host is built so the application exits with an attributed validation
error rather than failing during device setup.

The current unsupported categories are:

- cross-backend `graph.produce` for world graphs;
- live-camera viewport sources that do not yet expose an `IRenderNode`.

Do not add compatibility booleans for unsupported shapes. Extend the data
model and builder when the rendering path exists, then remove the corresponding
validation rejection in the same change.

## Shader build integration

`Puck.SdfVm.csproj` compiles SDF HLSL into committed SPIR-V and DXIL. Other
shader-owning projects may use different build integration; do not assume this
project's targets are shared. See
[`src/Puck.SdfVm/Assets/Shaders/README.md`](../src/Puck.SdfVm/Assets/Shaders/README.md)
for the live shader inventory and capability floor.

## Verification

Changes to the shared builder or engine contract require the Puck Post battery
selected through the `verifying-puck-changes` skill. Also run at least one
`world` graph from `docs/examples` through the host backend. Demo-only
decorators are verified by running the demo, not by adding a Post stage.
