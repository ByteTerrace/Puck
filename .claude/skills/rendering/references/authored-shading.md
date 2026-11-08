# Authored shading: admission and execution

`SdfShadeProgram` is a sealed presentation program with a private constructor.
`Compile` is its only admission door: expressions become typed SSA in dependency
order, exact binary32 arithmetic folds, commutative operands sort, common values
share a register, and only surviving outputs emit instructions. The world
vocabulary's `WorldShadeLowering` uses the ordinary parser, lexical scopes,
template/function expansion and compile-time loops; it does not run a second DSL.

The stamp and allowed phase reads are `SdfShadeOps.Inputs`; output members and
types are `SdfShadeProgram.OutputType`. A light's direction, reach, probe share
and visibility are evaluated once (`sdfLightInputs`) and read by both the default
response and the light phase's stamp: `light.visibility` is the share the default
response lets through (the cascade slot's, or the probes' where no cascade
measured it, for a directional; the probes' share for a traced point light; one
otherwise), and `light.occlusion` is the surface's ambient occlusion. There is no author buffer address or
neighbour read. Runtime parameters occupy a separate typed block. Structural
constants enter code; runtime data does not. Canonical words, parameter layout
and the live semantics fingerprint determine identity. Author names are reporting
metadata. Canonical documents retain a flat dependency-ordered value graph, so
common expressions do not expand exponentially when serialized.

`WorldShadeLowering` binds each runtime argument through an immutable typed view;
integer expectations cross `let`, `select`, conditional and function bindings.
One source argument may supply float and uint views without changing either slot.
Admission labels live parameters by their output uses, ignoring declaration order
and dead slots; symmetric uses order their data consistently. Raw definitions
cross the same null, type, shape and checked-arithmetic guards in `Compile`.

The canonical SSA receives deterministic register allocation into the named
set bounded by `MaxRegisters`. Evicted pure values are rematerialized from their
dependencies; admission prices that final instruction stream. Operands held by
an enclosing expression cannot be evicted, and a destination may reuse its dying
source. Output masks write phase outputs at each defining instruction, before a
temporary is reused. Excess held operands or work are refused, never spilled into
an interpreter array. The generated stamp,
registers and outputs are named fields selected by constant-index helpers; every
decoded register field is masked. CPU compilation does not prove zero driver
spill, which still needs step 2's measurements.

`SdfShadeOps.Table` owns the op names, arities, helper-closed work and HLSL
templates. `SdfShadeHlsl` derives both the interpreter switch and straight-line
emission from the same typed functions. Results and shared float helper returns
are `precise`; no contraction or reassociation is permitted. Host folding is
limited to exact IEEE arithmetic, comparisons and literal vector construction;
transcendentals stay on the device. Integer conversion is explicit: `quantize`
floors then bitcasts, and `hash` consumes unsigned bits. The shared stage
sanitizers replace non-finite outputs with defaults, clamp domains and normalize
normals. `SdfShadeHlsl.Invocation` generates the surface, light and compose doors
used by both the views and glass kernels. Glass supplies contact and thickness;
authored roughness drives both its light response and its optical frost and
environment. The standard water program keeps the underlying glass smooth while
its reveal supplies foam, selected by the `flow` template's structural glass argument.
The frame operations share the old layers' slot and transformed-static-draw
conventions. Each executed instruction increments `sdfWorkSteps`.

The standard programs live in `worlds/standard/shading.puck`, called by the
foliage, water and styles libraries and the debug world's exhibits.
`world.shade programs|layers` selects interpreted programs or the old layers for
comparison. A material with no program inherits `WorldRenderDefaults.Shade`
(`render.shade`); `world.shade default <program|document>` selects an admitted
named scene program or restores that document default. `SdfFrameBlock.WriteShadeDefault`
carries its identity and parameter range; `SdfWorldTables.PackShadeDefault`
uploads its code and parameters after the scene's appearance words.

`SdfAppearance.Pack` alone places admitted programs in the appearance tail.
Its material stride is generated from `MaterialVectorsPerEntry`. Row 32 carries
program and parameter ranges; row 33 carries demand and read flags. All program
operands and shape side-table addresses are slice-relative. `sdfShadeRead` checks
the relative range and the enclosing allocation before reading; shape helpers
receive a relocated address only after their complete side table passes it.
The shade fingerprint covers templates, generator code and the embedded field
helper include closure. It joins the renderer stamp, not the field identity.

Costs are checked sums over allocated instructions. `SdfShadeCost.Shape` charges
convex vertices, ellipse bisection, and every sweep strand/sample/refinement;
the ellipse and sweep loop bounds are generated from the evaluator constants.
`PixelWork` includes the bounded light walk, default phases and fixed stage work.
`MaxOps` bounds allocated instructions separately from weighted work. Admission
checks the allocated phases' `PixelWork` at `MaxLights` for a complete 8x8 group,
including both glass interfaces, against `MaxWork`. This admission bound is provisional;
it is not a measured timeout margin. `SdfShadeDispatch.Plan` partitions the full
padded extent into whole groups under that bound and the backend group-count
limit. Its `Record` door pushes each job's first group; both recorders use it.
The shared pixel entry remaps that ordinal to the original grid, preserving the
views filter's local lanes and barrier. `SdfWorldResidency.InFlightShadeIdentities`
holds each submission's identities on the residency that recorded it, until a
proof of completion removes them, and never asks a fence: a reusable slot fence
reads signaled after an abandoned submission and after a loss's teardown drain
alike. `SdfShadeSlots` is the one door: a recording captures its residency and
plan, only a pass that drew is armed (`ShaderPipelineRenderNode.SubmitPackageReadbacks`),
and the node's wait of the slot (`IRenderGraphPackageReadback.Completed`) or a
released recorder's next package frame (`SdfShadeReleases`) completes it. A device
loss before either proof marks the submission lost, and it stays reported for the
quarantine. Quarantine and specialization belong to the remaining
[shader plan](../../../../docs/plans/author-shaders.md).

Read flags come only from live input ops. `SdfAppearance.ResolveShadeReads`
selects reads from the active programs and inherited default or legacy layers;
emissive lanes remain independent. `SdfFrameBlock` writes clock and world lanes
only when those reads require them. Default identity and data invalidate shading;
its demand separately invalidates the surface pass. Shadow demand combines base wrap with the
program's proven wrap and tilt bounds; unproved normal or light response reach
demands every side. `sdfMaterialShadowDemand` drives cascade queries, including
the exactly opposite face for every-side demand. `sdfLightVisibility` keeps an
unqueried authored slot at zero even during a shadow handoff. `world.budget` reports admitted program identities,
author names, static cost and demand. `shade-cost` displays the sum of explicit
phase work over `MaxPhaseWork`; `shade-tier` shows a pixel's tier: grey for no
program, cyan interpreted, green specialized. There are no quarantined kernels yet.

## The three views and glass kernels

Every views and glass recording takes its kernel from one door,
`SdfShadeSlots.RecordPipeline`, which asks the residency's selection:

- **Plain** (`sdf-world-views-plain`, `sdf-glass-plain`, fixed kernels compiled
  with `SDF_SHADE_EMPTY`): a frame with no active program (the lever on layers,
  or no material and no default with a program). `sdfShadeBinding` folds to none,
  so every program branch of the stages compiles out, not only the interpreter.
- **Interpreter** (`sdf-world-views`, `sdf-glass`): a frame with active programs
  that no published specialized set holds, and `world.shaders.tier interpreted`.
- **Specialized** (`sdf-world-views.shade-<set>`, `sdf-glass.shade-<set>`):
  `SdfShadeHlsl.Specialize` spliced in as `isa/sdf-shade.hlsli`, each program an
  arm of straight-line `precise` phases chosen once a pixel by the identity its
  header carries (`SdfShadeProgram.IdentityVector`). There is no interpreter arm:
  one would keep the interpreter's registers allocated for the whole kernel. A
  set records only a frame whose active programs it holds
  (`SdfShadeKernels.Covers`).

`SdfWorldResidency.PrepareShade` is the one publication door, once a frame
after packing, on the frame the passes record. It requests the frame's active
programs together with the documents' declared ones (`SdfFrame.ShadeDeclared`,
`WorldRenderSettings.DeclaredShades`, the same set `world prepare` compiles),
compiles them off the frame thread on `ShaderCompileTurns` through the World's
`ShaderCompiler` (counted in `shaders.compiler`, a hit runs no tool), reflects
and builds the pipelines (`SdfShadePipelines`), and publishes only an arrival
whose identity (programs, op fingerprint, the interpreter's
`SdfKernelSet.ContentKey`) is still requested and which covers the active set.
A frame whose active set leaves the published one retires it at once, so the
interpreter shows an edit on its next frame. A recording pins the set it records
with; the pin moves to its submission and drops only on a completion proof,
abandonment before submission or a device loss, and a retired set is freed only
at zero pins (or at device teardown and the residency's final idle drain).
Publication moves no revision; `world.shaders.tier` alone invalidates the
shading passes. `world.shaders.status` lists each program's state.
`SdfShadeKernels.Plan` refuses a plan whose closure lacks the generated text: the
closure looks generated includes up by `Path.GetFullPath`'s spelling, and a key
spelled otherwise silently compiles the interpreter under the specialization's
name.

Check changes with the `shade-*` spelling pairs and `ShadeLoweringTests` in
`Puck.World.Transpiler.Tests`, plus `puck test` of those authored admission worlds.
The pairs compare canonical documents, program words, identities and costs.
`puck shaders generate --check` checks generated-table drift. CPU shader
compilation can check both backend formats. `SdfShadeDispatchTests` checks padded
group coverage and work bounds. GPU comparisons use fresh lockstep processes,
pinned camera/tick/exposure and settled meadow, forest-road, water and cel stills
on Direct3D 12 and Vulkan, with the debug sweep's still tolerance. Check backfaces
under wrap and animated normals, contact foam and static transformed flow, and
clock/world-lane/no-read cadence separately. `puck mesh identity --against`
checks that presentation changes leave the bake identity alone. Timing belongs
to step 2's measurement round.

`worlds/debug/shade-surface-checks.puck` isolates the shadow and glass cases:
its `shadeWrap` and `shadeTilt` state cells drive the panel's world lanes, a
blocker covers one panel, and shallow glass copies carry object-frame flow with
one rotated copy. Shadows are explicitly enabled; the minimal basis defaults
them off. Keep indirect lighting off for the isolated shadow witness, and check
the cel sphere with indirect lighting enabled separately.
`worlds/debug/shade-checks.puck` isolates cadence and the inherited default.
Use `world.row.set render shade` to select programs reading `clock`,
`lanes.world0`, or neither, and inspect the views pass while simulation runs.
The clock reader executes as ticks advance; the lane reader stands until
`shadeLane` changes; the neither-reader stands throughout. `SdfFrameBlockLawTests`
checks the corresponding clock/lane bytes and default identity directly.

Wait for `world.wait ready` before submitting simulation-routed setup commands
under lockstep. `WorldRenderProbe` includes `RenderGraphRuntime.Render` in readiness,
so it reports the dependency holding a tick, including probe convergence in a routed
view, instead of declaring a partially completed graph ready. A frame rendering
while simulation commands wait is not evidence of a device hang; inspect that
reason and the advancing work counters first.

Keep lockstep enabled from startup for fresh-process animation comparisons.
Pausing the boot world does not pause routed instances: inspect
`world.instance.status` and pause their schedules too when their lighting must
stand. A `world.wait captures` answer only means no request remains pending;
confirm the requested PNG and its capture completion line. A refused request
also drains that queue, including when convergence outlasts the host's capture
hold budget. After an unknown control outcome, read the current state before
moving a camera or issuing the next capture.

`latticeGradient` uses `sdfLatticeNoiseGradient3`, shared with the old flow layer.
`shape` is a coverage-distance operation: within its footprint neighbourhood it
evaluates the admitted primitive, and outside its bounding box it returns the
positive box lower bound, as the paint path skips that exterior. It is not an
exact outside-distance query. Its op records a footprint read, packs the bounds
with the descriptor, and charges the full helper even when the early-out wins.
`SdfPaintShape.Of` enforces primitive admission for both paints and shade shapes;
captured scale and primitive rotation both reach their point evaluation. Shade
definitions normalize through `SdfShadeShape.Canonicalize`, including convex
profile start and unused controls, before their canonical document is written.
