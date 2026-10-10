# SDF program specialization

Every SDF kernel in Puck evaluates the world's field through one bytecode
interpreter: `mapCore` in `field/sdf-map.hlsli` and its gradient twin
`mapGradCore` in `field/sdf-map-grad.hlsli`, each a loop with one `switch`
over the whole instruction set. DXIL has no real function calls, so every call
site of the interpreter is a complete inlined copy of it. This plan replaces that
arrangement with **program specialization**: the field code a world renders is
generated from the world's own program and compiled into its own kernels, the
way engines turn material graphs into shaders. The generic interpreter remains
as the fallback a residency renders with while a specialized pipeline builds,
the pattern Godot uses with its ubershaders and specialized pipelines.

The direction is decided. This page decides the unit of specialization, the
generator, the compile and cache pipeline, what stays generic, how the gates
apply, and the order of work. It is part of the [rendering
programme](rendering.md), and the
[rendering skill](../../.claude/skills/rendering/SKILL.md) holds the field
contracts every phase keeps.

## Implementation status

Nothing on this page has landed. It builds on the work that gives every kernel
one field-evaluation call site and splits the indirect receiver out of the views
kernel (described below as the call-site work), which has landed, so phases 1
and 2 can start. A scratch prototype of the generator,
outside the repository, produced the compile numbers quoted here; nothing of it
is meant to be merged.

## Why the interpreter costs what it does

A census of the pre-optimization DXIL of the full views kernel
(`sdf-world-views.comp`, `-fcgl`, every call inlined from `CSMain`) finds 23
copies of `mapCore` at about 44,000 IR instructions each and 4 copies of
`mapGradCore` at about 99,000 each: 1.41 million of the kernel's 1.49 million
instructions. Each copy carries the whole shape switch (`evaluateShape`, about
7,300 instructions inlined), because the shape type is a runtime value.

| Kernel (DXC `-O3`, cs_6_6) | DXIL | SPIR-V |
|---|---|---|
| `sdf-world-views` | 472–657 s, 3.06 MB, 1.6–2.0 GB peak | 43–49 s, 2.68 MB |
| `sdf-world-views-folds` | 228 s, 1.62 MB | 35 s, 1.68 MB |
| `sdf-world-views-core` | 133–149 s, 1.14 MB | 24–26 s, 1.45 MB |
| `sdf-indirect-trace` | 130 s, 1.14 MB | 80 s, 2.87 MB |

Half of the views kernel's DXIL time is LLVM jump threading over the
interpreter's switch lattice. Drivers pay again: compiling these kernels takes
minutes, cold pipeline builds stall GPU tests and first launches, and a cold
NVIDIA cache peaks at about 7.5 GB with four builds running at once.

The call-site work removes the copies rather than their content: every kernel
inlines `mapCore` once and `mapGradCore` at most once, with no `[noinline]`
boundary (NVIDIA driver 610.74 crashes on real SPIR-V calls in the Near path).
With it, the views kernel compiles in 8.0 s of DXC CPU time to 220 KB of DXIL,
and the field work moves to a receiver kernel of 51.8 s and 552 KB (69.3 s and
615 KB for the comparison receiver, built only on selection); the trace kernel
takes 31.3 s and 407 KB. The remaining call site is still a whole interpreter:
every opcode, shape and blend in the instruction set, whatever the world uses.

## The programs shipped worlds render

The census loads each world through `WorldDefinitionLoader` and
`PuckDocumentComposer`, emits its static placements through
`WorldPlacementStamper.EmitStatic` and its animated placements through
`WorldStampPool`, and emits every prototype alone at unit scale, the method
`puck creation stats` uses. It reads the packed segment directory, so a segment
is exactly what `SdfProgram.SegmentRanges` defines and the rigid-plan flag is the
one the kernels read.

| Emission | Instructions | Instances | Segments (rigid) | Distinct segment structures |
|---|---|---|---|---|
| Island (`puck.world.json`), static placements | 3,219 | 254 | 498 (295) | 21 |
| Moth courtyard, static placements | 4,893 | 571 | 713 (24) | 11 |
| Moth courtyard, the meadow's pooled blades | 11,964 | 3,988 | 3,988 (3,988) | 1 |
| The moth, one body | 991 | 179 | 328 (210) | 33 |

A segment structure is a segment's instruction headers with the data removed.
Across both worlds' emissions and every one of their 94 prototypes there are 61
distinct segment structures. Cut at each compose (a shape or a field pop) and
keyed by their point operations alone, those structures reduce to **7 distinct
point chains on the island and 11 across everything**, over 11 shape types.
The island's static program uses 6 shape types, 10 opcodes and 4 blends of the
instruction set's 21 shapes, 27 opcodes and 17 blends.

The census does not yet count what the engine emits beside placements (catalog
avatars, screens, glyph runs, field emitters, the coarse crowd) or the game
worlds under `Assets/worlds/games`, which load only through their compositions.
Phase 1's read-back counts all of them from the live program.

## The unit of specialization

A world's specialization is its **chain set** with its **vocabulary**:

- A *chain* is the run of instructions from a segment's start, or from the
  previous compose, to the next compose, keyed by each instruction's opcode and
  the header lanes that select code, never its data. A chain ending in a shape
  records only that it ends in a shape; the shape's type belongs to the
  vocabulary.
- The *vocabulary* is the set of shape types, blend operations and compose
  forms (union, smooth, morph, stairs and the rest) the program uses, including
  those reached through rigid-plan leaves and part programs.

The generated code evaluates each chain's point operations as straight-line
code, and shares one shape step, one pop step and one compose tail, each
switching only over the world's vocabulary. Rigid-plan segments, whose point
chains the host already collapsed into poses, run their shared leaf loop over
that vocabulary.

The alternatives were measured or ruled out on the code:

- **The whole program, straight-line.** The instance-mask walk, segment bound
  skips and tape pruning are what keep a sample from evaluating every instance;
  they must stay a loop over the directory. Static stamps also bake their
  placement into instructions, so the stream changes with every placement added.
- **Per creation or prototype.** The island's 71 creations carry 39 distinct
  instruction streams, most of them repetitions of the same few chains, and any
  sculpt changes one. The key is coarser than the code it selects.
- **The interpreter with constant headers.** Calling the whole interpreter step
  with each instruction's opcode, shape and blend as literals, and letting DXC
  fold the switches, is correct by construction but compiles the island's
  shadow kernel in 601 seconds: DXC inlines the entire step at every
  instruction before it folds anything.
- **Per segment structure, shapes inlined.** Each shape event costs about 300
  DXIL instructions of straight-line code, because the bound skip, tape checks,
  lane erosion and shape body are copied into every event: the
  island's 21 structures compile to roughly the generic interpreter's size, the
  61-structure union to 2.6 times it, and three of the union's SPIR-V compiles
  crashed DXC with an access violation.
- **Per instance group.** Pooled instances already share one structure (the
  meadow's 3,988 blades are one), so grouping adds nothing the chain key does
  not.

### How often the set changes

Data never changes the key, so most edits leave a specialization valid:

| Change | Program rebuilt | Chain set or vocabulary changes |
|---|---|---|
| A body moves, a look's render lanes move | No: dynamic transform rows | No |
| `world.row.set` moves, scales or recolors a placement or a shape | Yes | No |
| A placement of an existing creation is added or removed | Yes | No |
| A bake lands and its placements become camera-hidden | Yes | No |
| A body changes look, joins or leaves (stamp pool reconcile) | Yes | Only when the look's creation is outside the world's set |
| A sculpt adds a shape of a type the world already uses | Yes | Usually no: an existing chain |
| A sculpt adds a warp, a fold or a new primitive type | Yes | Yes |
| `world.reload` of an edited source | Yes | When the source added any of the above |

Because the shipped specialization covers every prototype a world declares, in
its static and pooled emissions, a look switch, an inhabited body or a dynamic
instance never needs a compile. Only authoring a chain or primitive the world
has never used does.

### What dispatch costs

The program packs a chain table: for each segment, its first chain and count,
then one 32-bit chain identity per chain. The specialized walk reads one word
and switches over at most the world's chain count, then runs one switch over
the world's shape types at each shape. The interpreter loads a 16-byte header
and dispatches through a 27-way switch at every instruction. On the island a
non-rigid segment averages about 5.7 instructions and one or two composes, so
the walk replaces about six dispatches with two or three narrower ones, and the
point operations between them run without a header load. Rigid segments keep
their leaf loop and lose only the full shape switch.

## Code generation

### The building blocks

Phase 2 turns the interpreter into the generator's library without changing
its behavior. The walk's state, now `mapCore`'s locals (the point, the
distance scale, the lanes and slot, the lane-erosion handoff, the fold walls,
the parity delta, the one accumulator and the field-scope stack), becomes one
`SdfWalk` structure, and its dual twin `SdfDualWalk` adds the Jacobian columns
and the gradient-selection state. Each opcode's case becomes a function over
that state (`sdfOpTranslate(inout SdfWalk walk, uint index)`, its twin
`sdfDualOpTranslate`), each primitive keeps its function (`sdfBox`, `sdfCapsule`
and the rest) and gains a uniform entry, and the compose tail (tape seed,
material winner, blend) becomes one function. The interpreter's `switch` and
`evaluateShape`'s are then generated, not written: a C# table in
`Puck.SdfVm.Model` names each opcode's, shape's and blend's scalar and dual
functions, `ShaderDeclarations` writes the dispatch includes from it, and a law
refuses a member of `SdfOp`, `SdfShapeType` or `SdfBlendOp` without a row, the
way `SdfEncodingProbeLawTests` refuses one without an encoding call. `SdfIsaHlsl`
stays the one owner of the instruction set's values.

### The generator

`SdfSpecializationHlsl` (in `Puck.SdfVm.Model`, compiling no shader) takes a
specialization set and writes one include, `sdf-specialized.hlsli`: the chain
switch, each chain's calls to its opcode functions in order, and the vocabulary
switches over shapes, blends and compose forms. It never writes arithmetic; every
statement it emits is a call to a building block, so there is no second copy of
the instruction set to drift. The field kernels' own sources compile with
`SDF_SPECIALIZED` defined and the generated include supplied in memory
(`ShaderCompilationRequest`'s generated includes), so a specialized kernel has
the generic kernel's entry point, interface and stamp, and the interface check
`SdfKernelSet.InterfaceMismatch` holds it like any reloaded kernel. With
`SDF_SPECIALIZED` defined, the interpreter's dispatch includes are not compiled
at all.

A specialized kernel keeps no `[noinline]` boundary, like every generic kernel:
with one call site per kernel the whole interpreter legalizes inlined in
SPIR-V, and NVIDIA 610.74 crashes on real calls in the Near path. A world's
field is a fraction of the interpreter's size: every chain-form kernel in the
prototype compiled to SPIR-V, while the larger shapes-inlined form crashed DXC
three times. The prototype's numbers include SPIR-V function boundaries that no
kernel keeps, so phase 3 measures the specialized views and receiver kernels
without them.

The C# half is `SdfProgram.Chains` in `Puck.SignedDistance`: the chain table the
program packs, the set it uses, and the key of each chain. Which header lanes
select code is a column of `SdfOpRoles`: the shape and blend lanes of a shape,
the compose form of a pop, the plane and axis selectors of `RotatePlane`,
`AxialProfile`, `Shear`, `RepeatPolar` and `WallpaperFold`. The seed of
`CellJitter` and `NoiseDisplace`, the octave count, and `GaussianPush`'s float
bits in the shape lane are data.

### The field contracts the generated code keeps

The generator changes only the order in which the walk finds its next piece of
code, so each contract in the rendering skill's "Field contracts that bite"
holds by construction, and the laws below hold it by test:

- **One accumulator.** The chains and the shared steps all write
  `SdfWalk.result`; a reset resets only the point. Intersections and field
  operations keep their order-dependent meaning because chains run in program
  order.
- **Field scopes and their Lipschitz clamps.** `PushField` and `PopField` are
  ordinary steps over `SdfWalk`'s one saved-accumulator pair; the pop reads the
  baked `1/L` from its data, and the final `stepScale` multiply stays in the
  walk's epilogue. Morph and stairs pops are compose forms in the vocabulary.
- **Fold walls and `sdfMarchAdvance`.** A log-sphere step updates the walk's
  wall state exactly as the case does now, and the epilogue publishes
  `sdfMapStepBound` and the fold globals from it. Marches call
  `sdfMarchAdvance` unchanged.
- **Tape pruning certificates.** Shape and pop steps still ask
  `sdfTapeShapeLive` and `sdfTapeDecided` by instruction index, read from the
  chain's position at run time; the tape pass compiles the same steps under
  `SDF_TAPE_BUILD`, so its recording hooks and the bit-equality of the tape's
  scalar answer with the full walk are untouched.
- **Masks and segments.** The directory walk, the instance-mask summary walk,
  the segment bound skips and the participation policies are the walk's driver,
  shared by both forms; only the per-segment body differs.
- **Rigid leaves.** The plan decode, the shared dynamic pose and the tight-sphere
  rejects stay the driver's; only the leaf's shape call goes through the
  vocabulary.
- **The dual and gradient paths.** `mapGradCore`'s collect, replay and full-dual
  modes are driver logic; each chain has a dual case calling the dual opcode
  functions, and the shape gradient goes through the vocabulary's gradient
  switch.
- **Work counters.** Every count site lives in a building block
  (`sdfWorkShapes` and `sdfWorkGradients` in the primitive entries,
  `sdfWorkSteps` at the marches' call sites), and the generator emits none, so a
  frame's counted work is the same whichever form renders it.

### Proving the generator equals the interpreter

- **The replay law.** A C# model walks a program the way the interpreter does
  and records, per segment, the building blocks it would call; the generator's
  emitted calls for that segment's chains must be the same sequence. It runs
  over every program `SdfEncodingProbe` builds and every shipped world's
  emissions, and a mutation that drops or reorders a step makes it fail.
- **The key law.** Data-only edits keep a program's chain set; a changed shape
  type, blend, compose form or code-selecting lane changes it; every `SdfOp`
  member has its lane classification; two chains of one set with the same 32-bit
  identity are refused by name.
- **The device law.** `SdfFieldDeviceLawTests` compiles its probe kernel
  (`sdf-field.comp.hlsl`) generic and specialized for each law program and runs
  both on Vulkan, Direct3D 12 hardware and WARP. Identity and material must be
  equal, distances equal within the law's existing tolerance against
  `SdfFieldEvaluator`, and the counted march steps, shape evaluations and
  gradients exactly equal.
- **The CPU reference** stays `SdfFieldEvaluator`, unchanged; it already holds
  the generic kernel through the device law, and now holds both.

## Compiling and caching specialized kernels

Specialized kernels are compiled in both places, for different reasons.

**At world compile time,** `puck compile --tree` computes each compiled world's
specialization set from every prototype it declares (static and pooled, at unit
scale) and from the engine's emitters' probe emissions, generates the include,
and compiles every field kernel for both backends through the shared shader
cache. The result goes into a store beside the world packages,
`Assets/worlds/sdf-specializations/<key>`, with a `puck.sdf.specialization.v1`
manifest naming the instruction-set fingerprint, the set and each binary's pin.
The compiled world records the key it needs in a new chunk, registered through
`CompiledWorldChunks.With` and, like `BAKE`, not derived on boot. A shipped
World therefore boots specialized with no compiler, which the
`no-device-compile` canary and qualification's hidden-compiler matrix already
require of every shipped shader.

**At run time,** the residency computes the live program's set on every
program upload. When no stored or cached specialization covers it and the World
has a compiler, `SdfSpecializationSource` generates the include and compiles the
field kernels for the host backend only, through the World's `ShaderCompiler`
and its content-addressed cache under the state root's `pipelines`. That needs
the building-block sources at run time, which the build does not ship today:
the World ships `Sdf/**/*.hlsli` beside the deployed bytecode. A World with no
compiler renders the generic kernels for that program and names why.

**Keys.** A specialization's key is the SHA-256 of the instruction-set
fingerprint (`SdfIsaFingerprint`), the building blocks' closure pins
(`ShaderSourceClosure`), the generator's version and the sorted set. A kernel
covers a program when the program's chains and vocabulary are a subset of its
own, so one stored set serves every program its world can emit and an editor
session can grow its set instead of replacing it.

**Leases.** Specialized pipelines are entries of `GpuPassPipelineCache` like
every kernel (`GpuPassPipelineKey.OfCompute` keys by bytecode, so no new cache
exists), leased per residency through `SdfWorldPipelines` beside the generic
set, built on the pool and released on device loss and disposal. Pipeline
creation is the only device work, and it follows `BuildConcurrency`.

**The swap.** `SdfWorldTables` chooses one coherent set per frame: every field
pass of a frame runs specialized or every one runs generic, because the tape
pass's certificates must agree bit for bit with the walks that consume them.
It selects the specialized set when it covers the uploaded program and all its
kernels are built, records the choice in the frame's pipeline generation, which
joins the cadence signature, and needs no device idle, since both sets bind the
same interface. Unlike a missing views variant today (`ViewsWaiting`), a
building specialization never holds a frame: the generic set is part of
readiness and always renders.

**Readiness and captures.** `SdfWorldResidency.IsReady` additionally requires
the boot program's specialization to be settled: built, refused, or absent with
a named reason. A shipped world settles as soon as its stored pipelines are
created, so its first ready frame is already specialized. A capture's readiness
waits until the current program's specialization has settled, inside the
existing build hold budget, so a capture is always rendered by a known form.

**Live edits.** An edit that keeps the set (most of them) never compiles. One
that adds a chain or a primitive renders generic at once and swaps when its
specialization builds. The wait is set by the slowest field kernel on the host
backend, compiled in parallel with the rest: with the call-site work landed,
under 20 seconds on Vulkan, whose SPIR-V receiver compiles in about 11 seconds
generic, and about a minute and a half on Direct3D 12 until the receiver's DXIL
(115 seconds generic) shrinks with its set. That is the budget phase 4 holds on
the lead machine; the editor never waits for it.

**Driver caches.** `GpuPipelineCacheFile` keys one file per device and deployed
kernel set and rewrites it whole after each miss, with no size bound. Specialized
pipelines get their own pipeline-cache files keyed by specialization key, so the
existing eight-file retention evicts an editor's discarded sets rather than
growing the engine's file; a shipped world's stored sets join its content key.

## What stays generic

- **The fallback kernels.** Every field kernel keeps a generic build with the
  whole vocabulary, compiled at build time from the same building blocks and
  the generated dispatch. The views kernel's `-core` and `-folds` variants, the
  `SDF_STRIP_HEAVY` and `SDF_STRIP_ALL_EXOTIC` gates and
  `SdfViewsKernelVariants` exist to make that fallback cheaper per program;
  specialization does that exactly, so they are deleted.
- **World-agnostic passes.** The instance cull, cull arguments, sky runs,
  composite, resolve, environment and screen-emission kernels evaluate no field
  and are compiled once.
- **Test probe kernels.** The proof kernels in `tests/Puck.World.Tests`
  evaluate arbitrary law programs and stay generic, apart from the device law's
  specialized compilations.
- **The CPU reference.** `SdfFieldEvaluator`, its bounds interpreter and the
  baker are fixed-point simulation code and do not change.

With the call-site work landed, each generic field kernel holds one interpreter
copy. The generic views kernel is about 8 s and 220 KB of DXIL, the receiver
about 52 s and 552 KB, and those are the kernels the build still compiles.

## Determinism and verification

Rendering is presentation, so floats are allowed, but these gates define
correctness and each applies to specialized code as follows:

| Gate | What it holds for specialization |
|---|---|
| `puck parity` | Both backends boot the parity world, which settles specialized before its first capture (compiled at run time, since parity runs where `dxc` exists), so stations compare specialized against specialized; one leg under `world.specialization off` keeps the generic kernels covered. |
| `puck counters --check` | Counted work is identical by construction, so no ceiling moves; a run that renders some frames generic and some specialized reads the same counts. A count that differs between the forms is a defect. |
| Canaries | The new `sdf-specialization` canary boots a world, adds a primitive with `world.row.set`, reads generic frames counted, then specialized ones after the build, and compares captures with `framesAgree`; `no-device-compile` boots a shipped world with `dxc` hidden and reads store hits with zero compiles. Both run on both backends with `--debug-layers`. |
| Device laws | The device law above, on Vulkan, Direct3D 12 hardware and WARP. |
| `SdfPassPlanLawTests`, `SdfPassBindingLawTests` | Unchanged plans and bindings: the forms share one interface. |
| `puck shaders generate --check` | Covers the generated dispatch includes and the chain-table accessors. |

Counted work stays per-backend deterministic because the counted quantities are
evaluations, not instructions: a march takes the same steps and a sample
evaluates the same shapes, whichever code computes them. Pixel equality between
the forms within a backend is not promised, since a driver may contract
arithmetic differently in different code, which is why captures wait for a
settled form.

New read-backs: `world.shaders.status` prints the residency's specialization
(key, chain and vocabulary counts, state, and the reason when generic), and the
`sdf.specialization` work source counts requests, store hits, compiles,
refusals and frames rendered by each form. `world.budget` prints the program's
segments, chains and vocabulary. The operator lever `world.specialization
on|off` forces the generic kernels, for the parity leg above and for comparing
the forms by hand; it is never a document field, because no world should want
the slower form.

## Expected effect

### Measured in the prototype

The prototype generated the chain form for two sets, the island's static
program (7 chains, 6 shape types) and the union of everything the census found
(11 chains, 11 shape types), and compiled four field kernels with the pinned DXC
(`.github/actions/setup-dxc`) under the build's recipe (`cs_6_6`, `-O3`), one
compile at a time. Part programs were
off in every column, since the prototype does not generate their steps, and
`mapGradCore` stayed generic. Instruction counts come from the DXIL
disassembly; sizes and counts do not depend on machine load, while the shared
machine's other builds make the times indicative only.

| Kernel | Generic interpreter | Island set | Shipped union |
|---|---|---|---|
| `sdf-world-shadow` | 11,579 instructions; DXIL 99 KB, SPIR-V 269 KB | 6,849; 65 KB, 163 KB | 9,891; 86 KB, 223 KB |
| `sdf-world-ambient` | 16,535; 132 KB, 377 KB | 7,621; 67 KB, 181 KB | 13,699; 108 KB, 299 KB |
| `sdf-beam` | 8,516; 72 KB, 220 KB | 4,500; 45 KB, 127 KB | 7,542; 66 KB, 186 KB |
| `sdf-world-primary` | 11,049; 97 KB, 276 KB | 6,381; 65 KB, 169 KB | 9,442; 88 KB, 230 KB |

Subtracting each kernel compiled with no field at all, the island's set removes
56 to 62 per cent of the field code at each call site, and the shipped union 14
to 22 per cent. These small kernels compile in 3 to 9 seconds in every form.

The full views kernel, measured before the call-site work with its 23 `mapCore`
copies, shows the effect on a heavy kernel. The prototype specialized only
`mapCore`, so its four `mapGradCore` copies (about 400,000 of the kernel's
pre-optimization instructions) stayed generic in every column:

| Full views kernel, DXIL | Generic interpreter | Island set | Shipped union |
|---|---|---|---|
| Pre-optimization IR, whole kernel | 1,222,640 | 933,300 | 1,160,563 |
| Pre-optimization IR, one `mapCore` copy | 32,397 | 19,817 | 29,698 |
| DXIL instructions | 300,785 | 216,046 | 274,056 |
| DXIL bytes | 2.61 MB | 1.89 MB | 2.34 MB |
| Compile time, under load | 575 s | 454 s | 727 s |

The island's set cuts each `mapCore` copy by 39 per cent and the kernel's DXIL
by 28 per cent with the gradient walk untouched. The union's vocabulary, with
its heavier primitives, keeps most of a copy's size, and its compile measured
slower than the generic kernel while another lane's compiles shared the
machine; its instruction count does not explain that, so phase 3 measures it
again on an idle machine. Generic and specialized SPIR-V compiled alike (the
views `-core` kernel: 55 s and 1.27 MB generic, 45 s and 1.22 MB specialized).

### Measured on the floor GPU

The existing strip tiers give a lower bound on what a smaller interpreter buys
in the hit passes, which today always compile the full instruction set (only
views has tiers). On the debug world at the medium preset, windowed at
1280×900 on the RTX 2060 under Vulkan, the primary and shadow kernels were
reloaded with each tier's strip macro and timed with `world.gpu-timing`. The
Moth needs the full set (Sweep, superellipsoid exponents, flare, shear), so the
stripped columns render it wrongly; they measure the interpreter's cost, not a
shippable result. March steps differed by less than ten per cent between
columns.

| Hit kernel | Full set | `SDF_FOLD_OPS` | `SDF_CORE_OPS` with scopes kept | `SDF_CORE_OPS` |
|---|---|---|---|---|
| `sdf-world-primary` | 27 ms | 21.8 ms | 12.4 ms | 8.6 ms |
| `sdf-world-shadow` | 77–85 ms | 73 ms | 64 ms | 54 ms |

Two thirds of the primary kernel's time sits in the cases beyond the core set,
the fold tier the largest share. A fixed tier keeps a whole family for one
member the Moth uses; a per-world specialization keeps only the cases its
program reaches.

### Estimated from those measurements

- **Build-time shader compile.** The build compiles only the generic kernels,
  minus the deleted views strip variants and their fade twins. World compile
  adds one specialized set per distinct world set: about fifteen field kernels
  for each backend, each smaller than its generic twin. The generic field
  kernels' measured DXIL times after the call-site work (receiver 52 s, trace
  31 s, classify and shade 23 s each, views 8 s, the other kernels 3 to 21 s
  each) sum to about three and a half CPU-minutes, so one specialized set costs
  less than that cold, spread over the build's compiler workers. The shared
  shader cache makes a rebuild
  with unchanged sets compile nothing, but every distinct set pays that once,
  which is why the set's scope is a decision below.
- **Per-program DXC.** An editor recompile builds the host backend's field
  kernels for the new set. Applying the full views kernel's measured ratio (21
  to 28 per cent less time and code with `mapCore` alone specialized) to the
  receiver's 52 seconds of DXIL, and a similar cut to the gradient walk, puts
  the slowest Direct3D 12 kernel near 35 to 40 seconds and every SPIR-V kernel
  under 20; the small kernels take 3 to 9 seconds either way.
- **Driver compile and memory.** Driver work tracks the DXIL it is given, and a
  per-world set hands it 33 to 49 per cent less per field kernel (13 to 18 for
  the shipped union), so cold pipeline builds and the cold-cache memory peak
  should fall in proportion. This is an estimate: the driver cannot be measured
  without a GPU leg, and phase 4 owes that reading on the RTX 2060 and the RTX
  4070.
- **Runtime GPU cost.** Counted work cannot move: march steps and shape
  evaluations are the same by construction, which is what keeps the gates
  stable. What falls is the cost of each evaluation, from about six header
  loads and full-width dispatches per non-rigid segment to two or three narrow
  ones, with straight-line point arithmetic and a smaller live register set
  between them. Phase 4 confirms it by disassembly of the per-chain paths and,
  when the owner asks, one `puck bench` reading on an idle machine; no
  wall-clock threshold is a gate.
- **Memory.** The chain table adds two words per segment and one per chain:
  about 1,300 words for the island's static program. A stored set costs its
  bytecode, a few megabytes per backend. Each residency keeps its generic set
  leased beside the specialized one, so field pipelines are held twice.

## Phases

Each phase lands green on its own, deletes what it supersedes in the same
change, and owes the gates named with it. GPU legs run under the lead's grant,
one at a time per GPU.

### Phase 1 — Chains and vocabulary in the program

The program learns its own chains; no kernel reads them yet.

- `SdfOpRoles` gains the column naming each opcode's code-selecting lanes.
- A new partial, `SdfProgram.Chains.cs`, builds the chain table from
  `SegmentRanges` and the rigid plan, computes each chain's key and 32-bit
  identity, refuses a collision inside one program by name, and exposes the
  program's specialization set (chains and vocabulary). The table packs after
  the segment directory; its lane constants live in `SdfProgram.IsaLayout.cs`,
  `SdfIsaHlsl` generates their accessors, `SdfEncodingProbe` gains a call that
  packs a chain table, and `sdfLoadProgramLayout` reads the new offset.
- Read-backs: `world.budget` and `puck creation stats` print segments, chains
  and vocabulary, which also closes the census's gaps (engine emitters, game
  compositions) from the live program.
- Laws: `SdfProgramChainLawTests` in `tests/Puck.SignedDistance.Tests` (the key
  law above), `SdfEncodingProbeLawTests` and the regenerated fingerprint.
- Gates: the SignedDistance and SdfVm suites, `puck shaders generate --check`,
  `puck lengths --check`; and, because the packed words and the fingerprint
  move, `puck parity`, `puck counters --check` and the field canaries on both
  backends.

### Phase 2 — The interpreter becomes its building blocks

The kernels behave exactly as before; the interpreter's cases become functions
and its switches become generated.

- `field/sdf-map.hlsli` and `field/sdf-map-grad.hlsli` split into the walk
  driver, `field/sdf-walk.hlsli` (`SdfWalk`, `SdfDualWalk`, the compose tail),
  and the opcode modules under `field/ops/`, each function with its dual twin.
  `field/sdf-shapes.hlsli`, `field/sdf-gradients.hlsli` and
  `field/sdf-parts.hlsli` call primitives through one uniform entry each.
- `Puck.SdfVm.Model` gains the building-block table (`SdfFieldKernels`), and
  `ShaderDeclarations` writes the generated dispatch includes from it.
- Laws: the table law (every opcode, shape and blend has a row, and every named
  function exists in the field tree), and a law refusing a hand-written
  `case SDF_OP_` outside the generated includes.
- Gates: `puck counters --check` reads exactly the recorded counts with no
  ceiling moved; `puck parity`; `SdfFieldDeviceLawTests`; the field and indirect
  canaries with `--debug-layers` on both backends. The commit records each
  kernel's DXIL and SPIR-V bytes and compile time against the parent; the
  prototype's refactor moved them by a few per cent.

### Phase 3 — The generator and specialized kernels, offline

Specialized kernels exist and are proved; nothing at run time selects them.

- `Puck.SdfVm.Model/Specialization`: `SdfSpecializationHlsl` and
  `SdfSpecializationKey`.
- `Puck.SdfVm/Specialization`: `SdfSpecializationCompiler`, which compiles the
  field kernels (a classification on `SdfKernel`) for a set through
  `ShaderCompiler` with the generated include, for one backend or both, and
  holds each result to its interface as `PrepareReload` does.
- The walk drivers gain their `SDF_SPECIALIZED` include points. The generator
  covers every field site the prototype left generic: the dual chains of
  `mapGradCore`, the part-program leaf step and the tape builder's hooks.
- The specialized views and receiver kernels are measured as they ship, with no
  SPIR-V function boundary.
- A verb, `puck shaders specialize --world <path>`, prints a world's set, key,
  and each specialized kernel's size and compile time.
- Laws: the replay law and key law in `tests/Puck.SdfVm.Tests`, and
  `SdfFieldDeviceLawTests.Specialized.cs` in `tests/Puck.World.Tests` (a GPU
  leg).

### Phase 4 — Selection, the swap and readiness

Residencies render specialized wherever a compiler can build their set.

- `SdfWorldPipelines` (a new `.Specialization.cs` partial),
  `SdfWorldPipelineCatalog`, `SdfWorldPipelineSource`,
  `SdfWorldTables.Pipelines.cs`, `.ProgramUpload.cs` and `.Cadence.cs`,
  `SdfWorldResidency` (a new partial) and `SdfWorldPasses`' capture readiness;
  `WorldBootComposition` registers the compiler and store for both presentation
  shapes; `WorldShaderReloadCommandModule` prints the status and carries the
  `world.specialization` lever; a reload rebuilds the current set from the
  reloaded building blocks.
- `build/Shaders.targets` ships the `Sdf` include tree beside the bytecode, and
  `GpuPipelineCacheStore` gains per-specialization pipeline-cache files.
- The rendering skill, `kernels.md`, the `Puck.SdfVm` README and the frame
  rendering handbook describe selection, the swap and the lever.
- Laws: `SdfWorldResidencySpecializationLawTests` over the fake device (generic
  renders while a set builds, the swap is frame-atomic, a refused set renders
  generic and names itself, readiness waits for a settled set, a capture waits
  for the current one), and `SdfPipelineBuildLivenessLawTests` extended.
- Canaries: the new `sdf-specialization`, and `no-device-compile` extended to
  name the generic fallback for an edit with no compiler.
- Gates: `puck counters --check`, `puck parity`, every GPU canary and the device
  laws on both backends, and qualification's hidden-compiler cells.

### Phase 5 — Shipped specializations

Shipped worlds boot specialized with no compiler, so the generic fallback no
longer needs its per-program variants.

- `src/Puck.Cli.Worlds/Transpiler/CompileCommand.Tree.cs` writes each compiled world's
  specialization into the store beside `WritePipelinePackages`, and
  `build/WorldAssets.targets` prunes stale entries as it does for packages.
- `WorldSpecializationClosure` in `Puck.World.Client` computes a world's set
  from its prototypes and its emitters' probe emissions.
- `Puck.World.Schema` gains the chunk naming the key, and `Puck.SdfVm` the store
  reader (`SdfSpecializationStore`), which refuses an altered binary by its pin.
- Deleted: `SdfViewsKernelVariant.cs`, the `-core` and `-folds` views kernels and
  their `SdfKernel` members, the strip gates, the variant chains behind
  `ViewsWaiting`, and `SdfViewsKernelVariantLawTests`; the rendering skill's
  kernel-tier step, `kernels.md`'s views-variant section and the sync-pairs row
  are rewritten in the same change. Deleting them earlier would leave a shipped
  World without a compiler on the full interpreter between phases.
- Laws: the closure law (every prototype's static and pooled emission, every
  look and every engine emitter's probe is covered), the chunk round trip, the
  store's pin refusal, and `puck compile --tree --check`.
- Gates: `no-device-compile` reads store hits with no compile, qualification,
  and `puck parity` specialized.

### Lanes

Phases 1 and 2 run in parallel: lane A owns `src/Puck.SignedDistance`'s program
files, `SdfIsaHlsl`, `SdfEncodingProbe` and the read-back sites; lane B owns
`src/Puck.SdfVm/Assets/Shaders/Sdf/field/**`, the building-block table and its
`ShaderDeclarations` entries, and touches no C# program file. Lane C (phase 3)
starts when both have landed and owns the two `Specialization` directories, the
verb and the device-law partial. Lanes D (phase 4) and E (phase 5) then run in
parallel: D owns the `Puck.SdfVm` runtime files, the shipped include tree and
the pipeline-cache store; E owns the compile tree, the closure, the chunk and
the store reader. Both register services in `WorldBootComposition`, and E's
deletions touch D's pipeline files, so D lands first and E merges it.

## Decisions

The engineering choices above follow from the measurements. The three product
trade-offs they left are decided by the owner:

- **The World ships `dxc`.** A shipped World carries `dxc` and `dxcompiler`
  (tens of megabytes), so an edit that adds a chain or primitive specializes in
  the field instead of rendering generic until the next package. The
  `no-device-compile` canary and qualification's hidden-compiler matrix keep
  their meaning for the shipped sets: a shipped world still boots specialized
  from its store with the compiler hidden, and the compiler serves only what the
  store does not cover.
- **One set per world.** Per-world sets give the smallest kernels (the
  island's cuts the field code by more than half) at four to five CPU-minutes
  of cold compile each; a tree-wide union, which would compile once and cover a
  portal or session into any shipped world, was measured at a field only 14 to
  22 per cent smaller than the generic interpreter's and is not taken. A portal
  or session into another world therefore builds that world's set.
- **An editor session's set only grows; a save rebuilds it to fit.** Growing
  the set across a session avoids recompiling when an author toggles between
  two sculpts; the kernels grow until the session ends. Saving the world
  computes the set the saved document needs and rebuilds it to exactly that,
  so a shipped set never carries a sculpt the author discarded.
