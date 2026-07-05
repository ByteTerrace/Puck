# SDF world render centralization plan

## Status (2026-07-04): phases 1–4 LANDED

- **Phase 1**: `SdfWorldRenderSpec` + `SdfWorldRenderBuilder` live in
  `src/Puck.Demo`. Two amendments over the original sketch: (a) the spec
  carries a CAPACITY ENVELOPE (`ProgramWordCapacity`/`InstanceCapacity`, plus
  the existing dynamic-transform floor) that flows into
  `SdfWorldEngineOptions` — a hot-swapping frame source declares its largest
  program up front instead of relying on program shapes staying identical
  (`UploadProgram` rejects an over-envelope program loudly); (b) ALL
  backend-specific choices (bytecode extension, child `directX` flags,
  decorator availability) derive from one spec field, `HostsOnDirectX`.
- **Phase 2**: `OverworldRenderNode` builds a spec and delegates; simulation
  concerns stayed put. `--validate-overworld` reproduces the pre-extraction
  state hash bit-for-bit.
- **Phase 3**: `WorldNode` runs again through the shared builder
  (`GraphBuilder.CreateWorldRootNode`) on the HOST backend. Deferred/retired
  affordances (cross-backend `produce`, the `child` boolean, `live-camera`
  viewport sources) are pre-flighted BEFORE the window host is built and
  rejected with attributed errors (exit 2) — `GraphBuilder.UnsupportedReason`
  is the one owner of that list.
- **Phase 4**: the document screen-source seam is data: a scene `screenSlab`
  takes an optional `screenIndex` + explicit `worldOrigin`/`worldRight`/
  `worldUp` frame, and the top-level `screenSources` table maps each screen
  index to a provider (`{ "$type": "viewport", "slot": n }` = the
  gaming-brick child's NATIVE framebuffer). `docs/examples/world-screen.json`
  is the live proof; the Post `world-screen` stage additionally asserts the
  quaternion-authored `ScreenSlab` overload is pixel-identical to the
  equivalent explicit frame.
- **Phase 5 (remaining open work)**: the live-camera child render node (the
  capture stack survives in `Puck.Platform`; its `IRenderNode` was deleted
  with the monolith), cross-backend `graph.produce` re-host, and hoisting the
  stale-bytecode guard + DXC compile pattern into a shared `.targets` for the
  other shader-shipping projects.

## Goal

Centralize the SDF world rendering concerns that the overworld prototype proved out,
then make `WorldNode` consume that same rendering path as data. Keep
`OverworldNode` as the simulation, input, and application root. The name of
`WorldNode` can wait; the immediate goal is one shared rendering host whose
inputs are explicit data.

Today, `SdfEngineNode` already centralizes the low-level backend-neutral SDF
renderer: frame source, child viewport surfaces, diegetic screen sources,
dynamic transform capacity, capture, and export mode. `OverworldRenderNode` still
owns important render assembly policy around child-node construction, screen
source providers, kernel/backend bytecode selection, fixed child allocation,
and optional overlay wrapping. Those rendering policies are the extraction
surface.

## Principles

- Keep `OverworldNode` responsible for overworld simulation: players, command routing,
  roster events, console boot state, cartridge shelf state, GamingBrick
  timeline assignment, choir parking, and debug commands.
- Keep `SdfEngineNode` in `Puck.SdfVm` as the backend-neutral render node. Do
  not move Demo-only node composition or app services into `Puck.SdfVm`.
- Start the shared render builder in `Puck.Demo`, because it needs to compose
  Demo-owned child render nodes such as `GamingBrickChildNode` and live camera
  nodes. Move lower only when dependencies are genuinely generic.
- Re-enable same-device `WorldNode` rendering first. Bring cross-backend
  `graph.produce` back later as an explicit producer/export feature.
- Prefer viewport-source data and screen-source tables over more booleans like
  `WorldNode.Child`.

## Phase 1: Define the render boundary

Introduce a small render-spec/options layer, likely in `src/Puck.Demo` at first.
Suggested shape: `SdfWorldRenderSpec` plus `SdfWorldRenderBuilder`.

The spec should carry:

- `ISdfFrameSource` frame source.
- output width and height.
- optional capture path.
- resolved host/backend choice.
- child viewport nodes keyed by viewport slot.
- screen-source providers keyed by SDF screen index.
- dynamic transform capacity floor.
- optional output image factory for future export mode.
- optional render-node decorator for wrappers such as the overworld binding bar.

Backend selection must be part of the spec. Today the overworld path hardcodes
SPIR-V kernels, passes `directX: false` to GamingBrick children, and uses Vulkan
services for the binding-bar overlay. The shared builder must choose `.spv` or
`.dxil` bytecode and child-node backend flags from the resolved host/device path.

Keep the binding-bar overlay as an optional decorator. It depends on overworld
input state and room-region data, so the core world renderer should not know
about it.

## Phase 2: Move overworld render assembly into the shared builder

Extract the pure rendering assembly from `OverworldRenderNode`:

- `SdfEngineNode` construction.
- `SdfWorldKernels.Load` and bytecode extension selection.
- child dictionary handoff.
- screen-source provider handoff.
- dynamic transform capacity.
- capture path.

Leave the overworld application concerns inside `OverworldRenderNode`:

- world stepping.
- player intent and roster sources.
- console boot state.
- cartridge library and insertion behavior.
- GamingBrick timeline assignment.
- choir parking.
- debug verbs and capture commands.

Then convert `OverworldRenderNode` to build a render spec from `OverworldFrameSource`,
`BuildConsoleChildren()`, `BuildScreenSources()`, and dimensions, and pass that
spec to the shared builder. The default Vulkan overworld behavior should remain
identical.

Add a clear backend-safety decision for the overlay. Either keep overworld forced
Vulkan until the overlay path is backend-neutral, or have the builder explicitly
skip or replace the overlay when hosting on Direct3D 12. Do not silently bind
Vulkan bytecode on a Direct3D 12 host.

## Phase 3: Re-enable WorldNode through the same renderer

Add `WorldNode` handling back to `GraphBuilder.Build`, routed through the shared
render builder.

Initial `WorldNode` support should use:

- `RunDocument.CreateFrameSource(document)` for document scene and viewports.
- `ViewportBuilder.ChildSources(document.Viewports)` to identify child viewport
  slots.
- a dedicated child-node factory to map viewport source data to `IRenderNode`s.

Existing data seams already identify `LiveCameraSource` and `GamingBrickSource`.
The child factory should handle ROM loading, backend-specific resample bytecode,
and allocation policy.

Treat `WorldNode.Child` as a compatibility shim or legacy field. New rendering
capability should flow through viewport source data such as:

```json
{
  "source": { "$type": "gaming-brick" }
}
```

or:

```json
{
  "source": { "$type": "live-camera" }
}
```

When re-enabling `WorldNode`, first render it on the host backend only, matching
overworld. Defer cross-backend `graph.produce` until the shared same-device render
host is stable; cross-backend production has extra LUID matching, device sharing,
export, and device-loss concerns.

## Phase 4: Make overworld render learnings data-addressable

Add a document-level model for diegetic screen sources. The renderer already
supports `screenSources`; the document needs a way to say which SDF `ScreenSlab`
samples which source.

Recommended shape: an explicit screen-source table keyed by `screenIndex`, with
providers such as:

- child viewport slot.
- native GamingBrick framebuffer.
- live camera frame.
- future named source ids.

Extend document scene `ScreenSlabObject` only after defining how its world-space
screen frame is derived. Start with explicit `worldOrigin`, `worldRight`, and
`worldUp` data for correctness. Add transform-derived convenience later if it is
safe and well validated.

Add child allocation policy to data where needed. Overworld uses fixed full-frame
allocation because regions animate. Static `WorldNode` documents can use exact
viewport extent for memory efficiency. If dynamic layouts become document-driven,
consider:

```json
{ "allocation": "region" }
```

or:

```json
{ "allocation": "frame" }
```

Keep dynamic transforms primarily program/frame-source driven. Static document
worlds should keep empty transforms. If data-driven moving entities become a
requirement, introduce an explicit animation/entity model rather than overloading
static scene objects.

## Phase 5: Clean stale model and docs after behavior exists

After `WorldNode` is runnable again:

- Update docs and comments that currently say Demo builds only `overworld` graphs.
- Keep historical notes only where they explain retired validation flags or Post
  ownership.
- Leave the `WorldNode` name alone unless the implementation makes the mismatch
  harmful. If renamed later, preserve `$type: "world"` as an alias or migration
  path for examples.
- Remove or deprecate stale `WorldNode` affordances that remain unsupported,
  especially if `graph.produce` stays deferred.

## Relevant files

- `src/Puck.Demo/Overworld/OverworldRenderNode.cs` - current home of proven render
  assembly: `SdfEngineNode`, child nodes, screen sources, overlay wrapper, and
  backend hardcodes.
- `src/Puck.SdfVm/SdfEngineNode.cs` - reusable low-level SDF world render node.
- `src/Puck.Demo/GraphBuilder.cs` - current graph switch; add `WorldNode` back
  here once the shared renderer exists.
- `src/Puck.Scene/NodeDocument.cs` - `WorldNode`, `OverworldNode`, `Produce`, and
  `Child` model.
- `src/Puck.Scene/RunDocument.cs` and `src/Puck.Scene/JsonSdfFrameSource.cs` -
  document scene/viewports to `ISdfFrameSource`.
- `src/Puck.Scene/ViewportBuilder.cs` and `src/Puck.Scene/ViewportSource.cs` -
  data seam for camera versus child viewport sources.
- `src/Puck.Scene/GamingBrickSource.cs` and `src/Puck.Scene/LiveCameraSource.cs`
  - child source data to map into render nodes.
- `src/Puck.Demo/GamingBrickChildNode.cs` - reusable child viewport renderer;
  currently supports fixed allocation and backend-specific resample bytecode.
- `src/Puck.Demo/BindingBar/BindingBarOverlayNode.cs` and
  `src/Puck.Demo/SdfParityProducers.cs` - overworld-specific overlay/render
  decoration; keep optional and backend-aware.
- `docs/examples/world-*.json` and `docs/examples/four-quad.json` - corpus to
  migrate from parse-only back toward runnable `WorldNode` examples.

## Verification

After extracting overworld render assembly:

```powershell
dotnet build src/Puck.Demo -c Release
dotnet run --project src/Puck.Demo -c Release -- --validate-overworld
```

After any `SdfEngineNode` or shared SDF render behavior change:

```powershell
dotnet run --project src/Puck.Post -c Release -- --filter world
```

Also run focused stages as relevant: `world-screen`, `world-child`, and
`dynamic-transform`.

After backend bytecode-selection changes, run a Direct3D-host smoke if supported
and run the full battery before claiming backend safety:

```powershell
dotnet run --project src/Puck.Post -c Release
```

After re-enabling `WorldNode` in Demo, run at least:

```powershell
dotnet run --project src/Puck.Demo -c Release -- --run docs/examples/world-single.json --exit-after-seconds 1
dotnet run --project src/Puck.Demo -c Release -- --run docs/examples/world-split.json --exit-after-seconds 1
dotnet run --project src/Puck.Demo -c Release -- --run docs/examples/world-menagerie.json --exit-after-seconds 1
dotnet run --project src/Puck.Demo -c Release -- --run docs/examples/four-quad.json --exit-after-seconds 1
```

If adding document screen-source semantics, add or extend a Post stage that
asserts observable pixels for a document-authored diegetic screen. Do not mock
internal calls or pin call shape.

If the run-document model changes, regenerate and check the schema:

```powershell
dotnet run --project src/Puck.Demo -c Release -- --emit-schema schema/run.schema.json
dotnet run --project src/Puck.Post -c Release -- --stage run-document
```

## Decisions (resolved with the 2026-07-04 landing)

- Binding-bar overlay backend support: the builder applies a spec's decorator
  ONLY on a Vulkan host and skips it with an explicit stderr notice on
  Direct3D 12 — never a silent Vulkan-bytecode bind. Revisit when the
  overlay's service bundle is backend-neutral.
- Live camera child node location: `src/Puck.Demo` (it composes platform
  camera services and backend resample bytecode). The node itself is still
  the open piece — until it exists, a `live-camera` viewport source is
  rejected at pre-flight with an attributed error.
- Screen slab authoring: the document model carries the EXPLICIT
  `worldOrigin`/`worldRight`/`worldUp` frame. The transform-derived
  convenience exists as the builder-side quaternion `ScreenSlab` overload,
  and the Post `world-screen` stage pins it pixel-identical to the explicit
  frame — the safe base for exposing transform-derived authoring to
  documents later.
