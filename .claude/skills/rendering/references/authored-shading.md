# Authored shading: CPU admission and generated semantics

`SdfShadeProgram` is a sealed presentation program with a private constructor.
`Compile` is its only admission door: expressions become typed SSA in dependency
order, exact binary32 arithmetic folds, commutative operands sort, common values
share a register, and only surviving outputs emit instructions. The world
vocabulary's `WorldShadeLowering` uses the ordinary parser, lexical scopes,
template/function expansion and compile-time loops; it does not run a second DSL.

The stamp and allowed phase reads are `SdfShadeOps.Inputs`; output members and
types are `SdfShadeProgram.OutputType`. There is no author buffer address or
neighbour read. Runtime parameters occupy a separate typed block. Structural
constants enter code; runtime data does not. Canonical words, parameter layout
and the live semantics fingerprint determine identity. Author names are reporting
metadata. Canonical documents retain a flat dependency-ordered value graph, so
common expressions do not expand exponentially when serialized.

`SdfShadeOps.Table` owns the op names, arities, helper-closed work and HLSL
templates. `SdfShadeHlsl` derives both the interpreter switch and straight-line
emission from the same typed functions. Results and shared float helper returns
are `precise`; no contraction or reassociation is permitted. Host folding is
limited to exact IEEE arithmetic, comparisons and literal vector construction;
transcendentals stay on the device. Integer conversion is explicit: `quantize`
floors then bitcasts, and `hash` consumes unsigned bits. The shared stage
sanitizers replace non-finite outputs with defaults, clamp domains and normalize
normals. Neither execution tier has been wired to a shading stage yet.

`SdfAppearance.Pack` alone places admitted programs in the appearance tail.
Its material stride is generated from `MaterialVectorsPerEntry`. Row 32 carries
program and parameter ranges; row 33 carries demand and read flags. All program
operands and shape side-table addresses are slice-relative. `sdfShadeRead` checks
the relative range and the enclosing allocation before reading; shape helpers
receive a relocated address only after their complete side table passes it.
The shade fingerprint covers templates, generator code and the embedded field
helper include closure. It joins the renderer stamp, not the field identity.

Costs are checked sums over canonical instructions. `SdfShadeCost.Shape` charges
convex vertices, ellipse bisection, and every sweep strand/sample/refinement;
the ellipse and sweep loop bounds are generated from the evaluator constants.
`PixelWork` includes the bounded light walk, default phases and fixed stage work.
The compile-time proof beside `MaxWork` covers `MaxOps` and a whole maximum-cost
8x8 group. This CPU admission bound is provisional; it is not a measured timeout
margin. The dispatch planner, quarantine, stage invocation and specialization
cache belong to the remaining [shader plan](../../../../docs/plans/author-shaders.md).

Read flags come only from live input ops. Appearance aggregates them alongside
the old layers until their migration. Shadow demand combines base wrap with the
program's proven wrap and tilt bounds; unproved normal or light response reach
demands every side. Surface-pass demand consumption and unqueried-shadow
zeroing remain stage work. `world.budget` reports admitted program identities,
author names, static cost and demand; CPU-only admission has no specialized,
pending or quarantined kernels.

Check changes with the ten `shade-*` spelling pairs and `ShadeLoweringTests` in
`Puck.World.Transpiler.Tests`, plus `puck test` of those authored admission worlds.
The pairs compare canonical documents, program words, identities and costs.
`puck shaders generate --check` checks generated-table drift. CPU shader
compilation can check both backend formats; it cannot establish GPU tier
equivalence or the planner's work bound.
