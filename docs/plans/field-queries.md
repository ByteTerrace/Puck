# Presentation field queries

Two presentation features ask the world's signed distance field where the
nearest surface is, on the frame thread, every frame: the creation effectors'
surface probes, which plant feet (`WorldCreationInstances.ApplyEffectors`,
through `WorldSurfaceQuery.March`), and the chase camera's clearance sweeps
(`WorldCameraClearance`). Both walk the fixed-point interpreter
(`SdfFieldEvaluator`) over one program that holds the whole static world.
[The CPU frame](open-items.md#the-cpu-frame), item 4, names two replacements.
This page weighs them, chooses one, and gives its design, its invariants and
its build order. Another model family reviews it before any code is written.

These are presentation queries. Nothing here moves the simulation's
fixed-point contact.

## Summary

- **The choice is the compiled CPU reader, not the GPU batch.** A GPU answer
  arrives at least a frame after the question, which shows exactly where the
  field is discontinuous (a stair edge under a foot, a wall the camera swings
  into), turns every present and future presentation query into an
  asynchronous one, and still needs a CPU path for every host without a
  device. The CPU answer is synchronous, runs on cores the frame leaves idle,
  and is one path on every host.
- **The measurement moved the design.** A query's cost today is not the
  arithmetic of the shapes near it. It is that every sample walks a program
  the size of the world: about 1,500 instruction visits and 1,900 bound tests
  a sample, where the shapes that can matter to a foot fill about ten
  instructions. So the compile step that matters most is pruning the program
  per region, and vector code comes second.
- **The design has two parts.** A tree of cells holds, for each region of the
  world a query touches, the short list of instructions that can change the
  answer there (a tape). A float reader, generated from the same formulas
  that generate the fixed-point reader and the kernels, evaluates a tape over
  several points at once in vector registers.
- **The client's evaluator goes.** The presentation field stops constructing
  `SdfFieldEvaluator` at all. Its certificates and cull tables, about 44 MB
  for the debug world's placements alone, are replaced by the compiled
  program and the cells a query has touched.
- **What it opens.** The float reader is the CPU twin of the bake kernel, so
  the mesh audit and the CPU bake can move to it and the fixed-point reader's
  render reading can be deleted. The cell tree does not care which reader
  evaluates a tape, so contact can adopt it with the fixed-point reader, but
  contact needs it less than the packet assumed: the server already keeps one
  small field per collider. Both are steps for the lanes that own those areas.

## What a query costs today

The numbers below come from a scratch program that built the placement half
of the debug world's presentation field as `MotionClearance.TryCreate` builds
it (the solid placements `WorldPlacementStamper.Queryable` admits, emitted by
`WorldPlacementStamper.EmitStatic`), constructed `SdfFieldEvaluator` over it,
and read `SdfFieldEvaluator.PointWork` and `SdfQueryWork.ProcessWork` around
one query at a time. They are counts of the world's documents, not timings.

| Reading | Count |
|---|---|
| Solid placements the field admits | 1,339 of 1,340 |
| The one program | 124,106 instructions in 17,601 instances |
| Bytes the evaluator retains beside the program | about 44 MB |
| One foot probe (a 1.2 m ray, then the gradient at its hit) | 8 point evaluations, about 11,700 instruction visits and 5,600 bound tests |
| One 6 m camera sweep | 5 point evaluations, about 8,000 instruction visits and 9,300 bound tests |
| Instances whose own value at a point is within 3.5 m of the nearest instance's, at five points | 2 instances, 8 or 9 instructions |
| The same within 6.9 m | 2 to 11 instances, 9 to 108 instructions |

Two facts explain the first rows. Every point evaluation first ranks every
cullable instance in the world against the point (1,861 tests, in
`SdfFieldEvaluator`'s point prepass), and then visits about 1,500 instructions
that its certificates could not skip. A spatial index over instance bounds
would not fix this: the instances whose bound sphere reaches a ball 1.5 m
about a foot own about 1,100 instructions, because large terrain pieces bound
every point of the valley. Bounds say what could be near. Only the field's
value says what is.

The program's shape also decides which pruning is sound. Its root is not a
plain union: 15,733 of its 17,511 root shapes join by smooth union, with
radii from 0.14 m to 0.40 m. A smooth union depends on the order of its
operands, so a far shape cannot be dropped just because a nearer one exists.
[A tape per cell](#a-tape-per-cell) gives the rule that holds for this case.

## The two replacements at a town's scale

A town's feet are hundreds of probes a frame. The table reads both options
there.

| | GPU batch, answered late | Compiled CPU reader |
|---|---|---|
| When the answer arrives | At least a frame later. In this engine a readback is read when its fence signals (`ShaderPipelineRenderNode.PollReadbacks`), and background GPU work (`SdfGpuWork`) is paced by the frame's spare time, so the delay is more than one frame and varies. | In the call. |
| What the delay does | A foot swinging onto a stair reads the tread it left. A camera turned into a wall is inside it until the answer returns. Both are the cases the feet and camera captures exist to catch. | Nothing; the eye keeps the limit `WorldCameraClearance` states for every frame. |
| What callers become | Every query, and every authored program that asks one, is asked now and answered later. The camera's rise search, which bisects on the previous answer, must guess its candidates ahead or spread over frames. | Unchanged: ask, read, decide. |
| Which hardware pays | The GPU, which is the device the frame bar is read on. | CPU cores the frame leaves idle, once the frame fork (the packet's item 2) spreads the pack's registrations across the pool. |
| Hosts without a device | Need a CPU path anyway (a headless host, tests, a server), so the interpreter stays and there are two implementations. | One path everywhere. |
| What it needs resident | A second program on the device (the static solids alone, which is what the queries read) and the interpreter kernel's pipeline, in a player's process that otherwise never bakes. | The compiled program on the host. |
| Raw throughput | Highest: hundreds of marches are little work for a GPU. | Enough, by the counts: with pruned tapes a probe is a few hundred vector operations. Step 6's timing is what confirms it. |
| What else it serves | Nothing a host without a device can use. | The audit and the CPU bake, and contact where one collider's field is large. |

The GPU option is real. The bake kernel already samples arbitrary point lists
with gradients (`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-bake.comp.hlsl`'s
point mode), so a batch of probes could ride it with no new kernel. It loses
on what a player sees and on what it costs to keep. The decision register
allows a simulation tick pipelined beside the frame but not latency in what
the view shows of the hand
([the pipeline foundation](../decisions/rendering.md#the-pipeline-foundation)):
the camera reads input at present time, and a clearance that answers late
breaks that. A second implementation that still needs the first as its
fallback has to pay for itself (AGENTS.md, the bars). If the CPU path answers
a town's feet in well under a millisecond, as the counts predict, there is
nothing left for the GPU path to buy.

Weighed at the scale Puck is for: every zone is a world, and a client in a
town holds hundreds of feet over a field as large as the zone. The cost of a
query has to follow the neighbourhood, not the zone, on every host that asks
one, with a device or without. The server's contact already has that shape:
it keeps one field per collider and narrows them by a grid
(`FixedColliderGrid`), and a headless boot of the debug world reads about 60
instructions a point evaluation with no world-wide prepass
(`world.counters sdf.point`). Presentation is the reader that still walks the
world. The CPU option gives it the server's shape; the GPU option improves
one kind of host and leaves the walk where it is for the rest.

One version of the CPU option is ruled out by the tree itself. Emitting a
specialized method per program at run time
([the external survey](field-programs/research-elsewhere.md) raises it, after
Fidget) needs dynamic code, and every project is built AOT-compatible by
default (`Directory.Build.props`). The design below compiles ahead of time
instead: the build generates the reader, and at run time a program is lowered
to data the reader walks.

## The design

### A tape per cell

The program is a stream. Each top-level chain of point transforms ends in one
or more shapes that join a running distance (the accumulator), and a field
scope joins as one candidate when it pops. Call each such join an operand.
The debug world's field has about 17,600 of them.

For a cubic cell of the world, a tape is the list of chains that can change
the field somewhere in that cell. Every other chain is dropped. A query finds
the cell its sample point lies in and evaluates only that cell's tape.

Cells form an octree over the program's bounds. A child's tape is pruned from
its parent's, so no cell below the top walks the whole program. A cell is
built the first time a query touches it, by evaluating its parent's tape at
its centre, and it stops dividing when its tape is short or it reaches the
smallest size. Cells high above the ground stay large and nearly empty, so a
long ray through open air touches few of them.

**The prune rule.** Let `c` be the cell's centre, `ρ` its half diagonal plus
a small margin (so the taps of a gradient share one tape), and `L` the
program's certified Lipschitz bound (the reciprocal of `SdfProgram.StepScale`,
which bounds every candidate that joins the root by union or smooth union).
For an operand `u`, let `v(u)` be its candidate's value at `c` and `k(u)` its
smooth radius, zero for a hard union. Let `m` be the least `v` over the run,
and `K` the widest radius among the operands kept. An operand is dropped when

```text
v(u) - k(u)  >  m + W,     W = 2·L·ρ + 2·K + (K / 2.4) · ln((2·L·ρ + K) / τ)
```

where `τ` is the tolerance the dropped operands may move the answer by, a
micrometre. `K` and the kept set depend on each other, so the rule takes
operands in rising order of `v − k`, widens `K` as each is kept, and stops at
the first one past the cutoff. A chain is dropped when all its operands are.
For a hard union `K` is zero, `W` is `2·L·ρ`, and the rule is exact.

A run is a stretch of the stream whose operands all join by union or smooth
union. An operand that joins any other way (a subtraction, an intersection, a
chamfer, an edit of the accumulator) is kept in every tape and ends the run.
A run that starts from an accumulator earlier operands left counts that
accumulator as one more operand of radius zero. The debug world's field is
one run.

**Why it holds.** Write `S(a, c, k)` for the smooth union of accumulator `a`
and candidate `c`, and `g` for the weight it gives the candidate. Three facts
about the shared formula (`SdfFieldFormulas`' smooth minimum) carry the
argument:

1. `S` returns `a` exactly when `c ≥ a + k`. So an operand has no effect
   unless `c − k` is below the accumulator when it joins, and the accumulator
   is never below the least `c − k` it has joined (the bound
   `SdfFieldEvaluator`'s cull already rests on).
2. `S` lowers the accumulator by `k·g²`.
3. `S` is nondecreasing in `a` with slope `1 − g`, and the slope falls as `a`
   rises.

Take any point `x` of the cell. Values move by at most `L·ρ` from the centre,
so the nearest operand reads at most `m + L·ρ` and a dropped operand reads
more than `m + W − L·ρ` after its radius. Once the nearest operand has joined,
no dropped operand can have an effect, by fact 1. Before that, dropped
operands can lower the full program's accumulator, but only while it is above
`m + W − L·ρ`. When the last of them has acted, the full accumulator and the
tape's differ by at most `2·L·ρ + K`. From there both see the same operands,
and the full accumulator must still come down by at least `W − 2·L·ρ − 2·K`
to reach the nearest operand. By facts 2 and 3 each step shrinks the
difference by `1 − g` while lowering the accumulator by `k·g²`, and
`ln(1 − g) ≤ −2.4·g²` on the whole range, so the difference ends below
`(2·L·ρ + K) · exp(−2.4 · (W − 2·L·ρ − 2·K) / K)`, which is `τ` for the `W`
above. In plain words: a smooth union forgets what it held as it descends,
and the cutoff leaves it far enough to forget. A hard union that takes over
forgets everything at once.

Two things keep that bound from growing. No blend moves its result by more
than the accumulator it was given moved, so an operand after the run carries
a difference of `τ` forward without enlarging it. And a child prunes from its
parent's tape, so each level adds at most `τ`: a leaf fifteen levels down is
within fifteen micrometres of the whole program, against the millimetre at
which a march accepts a hit (`SdfFieldMarch`'s hit epsilon).

For the debug world's radii and a 2 m cell, `W` is about 7 m. The scratch
reading's estimate at that reach kept between 9 and 108 instructions, against
about 1,500 visited today. The estimate compared whole instances by their own
value; the rule itself is step 3's to measure.

**What it costs to build.** The first query in a new part of the world builds
the path from the root to its cell. The top levels still hold most of the
program, so that first path is on the order of a few hundred thousand
instruction evaluations. The field's build already runs off the frame thread
(`WorldFramePresenter.BuildStaticField`), and it warms the paths around each
local seat there. A later first touch descends from a cell that exists and
costs what that cell's tape holds. Cells are immutable once published and are
published by compare and exchange, so queries from any thread share them
without a lock.

### The float reader

The reader evaluates a tape at several points at once, one point per element
of a hardware vector (`Vector<float>`: eight where the CPU has AVX2). It is a
third reader of the instruction stream beside `SdfFieldEvaluator` and the
kernels' `mapCore`, and it reads the field as the kernels do, in single
precision.

- **Shapes and blends are generated.** `SdfFormula` lowers each shared
  formula to fixed-point C# and to HLSL today. It gains a third lowering, to
  vector C#, in which a condition is a mask and an early return is a select.
  `SdfFieldCatalogueGenerator` emits the reader's shape and blend dispatch
  from the same catalogue entries, so a formula written once reaches all
  three readers.
- **The walk and the escapes are written by hand.** The stream's own
  semantics (the point reset and root frames, transforms, folds, warps, field
  edits, the masked relief and its noise, scopes) and the shapes whose
  readers need a table or a loop (the convex polygon, the sweep, the path,
  the ellipse's projection) have no shared definition to generate from. The
  reader covers at least every op and shape the contact reading admits, so
  nothing the presentation field reads today is lost.
- **Every catalogue entry chooses.** The op and shape catalogues gain a facet
  that names the entry's vector reader or refuses it. A new op cannot be
  added without the choice, because the facet is abstract. A program holding
  a refused op is refused by that op's name when it is compiled, and its
  placement is excluded and reported through the channel that exists
  (`world.presentation-field`), as `WorldPlacementStamper.Queryable` does
  today.
- **Rigid chains run as one step.** `SdfProgram.CompileRigidPlan` already
  collapses a static translate and rotate chain into one pose and a shape for
  the kernels' rigid walk. The compiled program reuses that plan, so the
  common chain is one transform and one shape, not five instructions.
- **No per-point branching.** A tape is already pruned for its cell, so the
  reader runs it straight through for every element of the vector. That is
  what lets one dispatch per instruction serve eight points.
- **Points are relative to the cell.** A leaf rebases its chains to the
  cell's centre when it is built, and a sample is the offset from that
  centre. Single precision then resolves micrometres wherever the cell is,
  instead of losing bits to the distance from the world's origin.

Where the vector width pays: the gradient at a hit is four tetrahedral taps
(the kernels' own stencil, `SdfMeshBakeTaps.Offsets`) in one evaluation
instead of six walks; a cell's eight children are pruned by one evaluation of
its tape at their eight centres; and a lattice consumer (the audit, the CPU
bake) fills the vector. A single march sample uses one element. With a tape
of tens of instructions that is still under a microsecond's work, which is
why callers keep asking one question at a time.

### The query surface

One new type holds the compiled program and the cell tree and answers in
`float` and `Vector3`: the distance at a point, a ray or sphere cast, an
overlap, and the gradient at a point.

- **One march.** A cast runs `SdfFieldMarch.Run`, the march the exact and
  banded evaluators already share, with the float reader as its sampler. The
  accept threshold, the scaled advance and the exhausted outcome stay written
  once.
- **Held answers are unchanged.** `WorldFieldAnswer` stays the one door, with
  its reuse rules (`WorldSurfaceQuery.Covers` and the clearance margin). The
  field's instance is still its change revision: it is immutable and replaced
  whole when the static scene changes.
- **Counted.** `world.counters world.field-queries` keeps its families. A new
  counter set reads cells built, operands evaluated to build them, tape
  instructions evaluated and samples, so instructions a sample is a count any
  machine can read.

### What the client holds

Every reader of `WorldClient.StaticField` moves to the new type: the
clearance and the probes, a pick's surface (`WorldFramePresenter.TrySurface`),
the editor's pointer (`WorldEditorPointer.Cast`), a routed scene's dressed
field (`WorldSessionSceneEmitter`), and the motion check's clearance
(`MotionClearance`), which exists to read the same geometry the seat camera
does. `WorldPlacementStamper.Queryable` admits a placement by compiling it
for the float reader instead of constructing an evaluator for it.

The client then constructs no `SdfFieldEvaluator`. What it holds for the
field is the compiled program (the instruction lanes and side tables, a few
megabytes for the debug world) and the cells queries have touched. The
fixed-point evaluator's frame limit, which refuses a placement whose copy
stands beyond what its carrier can express, has no counterpart in the float
reader.

## Invariants

| Invariant | What enforces it | Where it stops |
|---|---|---|
| A float answer never reaches simulation state. | The type: it answers in `float` and `Vector3` and implements none of the deterministic query interfaces (`IWorldQuery`, `IFieldEvaluator`), so the contact seam cannot be handed one. `puck test --reproduce` shows state is unmoved. | A caller could convert a float answer by hand; that is rule 4's ordinary reach, with no new door. |
| The float reader agrees with the fixed-point reader and the kernels within a stated tolerance. | Shape and blend arithmetic is generated from one formula. The catalogue's abstract facet makes every op and shape choose a reader. A CPU law samples every admitted op, shape and blend against `SdfFieldEvaluator` at a centimetre and a metre. `SdfFieldDeviceLawTests` gains the float reader as a third column on both backends. | The hand-written walk and escapes are a sync pair, as the fixed-point reader's are; they join the review-only row of [silent invariants](../development/hazards.md#silent-invariants) until the law covers them. The tolerance is a distance, not bit equality. |
| A cell's tape answers as the whole program does, within `τ`, at every point of the cell. | The prune rule lives in one function, and a tape is made only by it. A law compares pruned and unpruned answers on random programs and on the adversarial ones: operands sorted far to near in steps smaller than a radius, radii at their extremes, and a hard run, which must agree to the bit. | It rests on the program's Lipschitz bound, as the march's step does: a shape that overstates its distance already lets a march tunnel, and would let the prune drop a live shape. `SdfPrimitiveLipschitzLawTests` is the check on that premise. Operands joined by other blends are never dropped, so a world built on root subtractions prunes less. |
| A sample reads the tape of the cell it lies in. | The lookup is the only way to a tape, and it descends by the point. A gradient's taps use their centre's cell, which the margin in `ρ` covers. | A tap farther from its centre than the margin would need its own lookup; the gradient's step is fixed below it. |
| A held answer is reused only over the field it was asked of. | `WorldFieldAnswer.Ask` compares the field instance, as today. | Unchanged. |

## Build order

Each step ends with a check that shows it worked. Timings are not among them:
they are a measurement request for the lead's quiet window.

1. **Lower the shared formulas to vector C#.** `SdfFormula` gains the third
   target and the generator emits the vector shape and blend dispatch.
   *Check:* the projects build, and a law samples every formula shape and
   every blend through the generated vector code against `SdfFieldEvaluator`.
2. **The walk, the escapes and the catalogue facet.** *Check:* the same law
   over every op and shape the contact reading admits; a program holding a
   refused op fails to compile for the reader with that op's name; the debug
   world's presentation field excludes no placement it admits today.
3. **The cell tree and the prune.** *Check:* the pruned-against-unpruned law
   above. On the debug world's field, built offline as the scratch reading
   built it, instructions a sample fall from about 1,500 to tens, and no
   bound test remains.
4. **The query type.** Casts through `SdfFieldMarch.Run`, the four-tap
   gradient and the counters. *Check:* the offline reading's foot probes and
   sweeps hit where `SdfFieldEvaluator`'s do, within the march's accept
   threshold, and the counter set reads their work.
5. **Move every reader and delete the client's evaluator.** *Check:* no
   presentation project constructs `SdfFieldEvaluator` for a field
   (`puck references`); the bytes the client's field retains, read as the
   scratch reading read the evaluator's 44 MB, are under a quarter of it; the
   debug world boots headless.
6. **Windowed verification, by the lead.** Feet on a slope and on the stairs
   (`debug.go stairs`) and the camera against walls read as before;
   `world.counters world.field-queries` beside the new set over the benchmark
   route; `host.frames`' capture part before and after, from frozen builds;
   `host.memory` before and after; and `SdfFieldDeviceLawTests` with the
   float reader's column on Direct3D 12 and Vulkan.

## What it serves after this lane

- **Contact (the physics lane).** The server's samples already cost what one
  collider holds, so the tree is not owed there. It would matter for a
  collider whose own field is large, such as a terrain mass of thousands of
  blended shapes. The cell tree is indifferent to the reader, so such a field
  can prune the same way with the fixed-point reader, the prune itself in
  fixed point with directed rounding so the same documents give the same
  tapes on every machine. *It would show:* `sdf.point.instructions` per
  evaluation for the largest field collider, read from `world.counters`.
  *It decides:* whether certified contact's cost is the walk or the bounds.
- **The bake's CPU twin (the rendering lane).** With a material beside the
  distance and the render-only ops, the float reader samples what the bake
  kernel samples. `SdfMeshCpuField`, the mesh audit and the CPU bake move to
  it, and `SdfFieldReading.Render` with the fixed-point render ops is
  deleted. *It would show:* `puck mesh audit --cpu` passing the same shapes
  from the float reader. *It decides:* whether the fixed-point reader shrinks
  to contact alone.
- **Batches across owners.** If the counters show marches still matter at a
  crowd's size, the feet of one creature march as one vector. Nothing in the
  design has to change for it.
- **A distance brick per cell.** `SdfDistanceGrid` keeps exact corner values
  so a march reads a bound far from surfaces. A cell could hold the same in
  single precision. It waits until the counters name the march's empty
  stretch as the cost.

## Systems considered

| System | What it does | Why it is not the answer here |
|---|---|---|
| `SdfFieldEvaluator`'s culls (its certificates and segment hierarchy) | Skip, per sample, what cannot change that sample's answer. | The skipping is decided again at every sample over the whole world: 1,861 bound tests and about 1,500 visits. The cell tree decides once per region. |
| `SdfDistanceGrid` and `SdfBandedFieldEvaluator` | A lattice of exact values, filled on first touch, that lets a march read a bound far from surfaces. | It cuts how many exact samples a march takes, not what an exact sample costs. Used by the server (`WorldSolidField`); a later layer here. |
| `FixedColliderGrid` | The static colliders near a region, by their bounds. | Measured: bounds keep about 1,100 instructions near any foot, because terrain bounds reach everywhere. The prune needs values. It also lives in the physics layer, in fixed point. |
| `SdfProgram.Excerpt` | A program of some of another program's instructions. | It builds and validates a whole program per excerpt. A tape is a list of ranges into one compiled program. |
| `SdfProgram.CompileRigidPlan` | Collapses rigid chains to a pose and a shape for the kernels. | Reused, not replaced: the compiled program reads it. |
| The bake kernel's point mode and `SdfGpuWork` | Samples point lists on the GPU off the frame. | It is the GPU option, weighed above. |
| `SdfFormula` and `SdfFieldCatalogueGenerator` | One formula and one catalogue entry generating both readers. | Extended, not replaced: the third lowering and the new facet go here. |
| `WorldFieldAnswer` | Holds an answer on its owner and proves when it may be reused. | Kept as it is. |
| `GravityOctree` | Octree scaffolding for the gravity solvers. | It partitions bodies in fixed point for a solve that rebuilds each tick. The cell tree is lazy, immutable and keyed by field values; they share a shape and no code worth joining. |
| `IByteVectorLanes` in `Puck.Maths` | Width-generic byte kernels. | The float reader needs only `Vector<float>`, whose width the runtime already abstracts. |
| The interval walk (`SdfBoundsCache`, the evaluator's box walk) | Encloses the field over a box. | It could shorten tapes exactly, but it needs the evaluator's certificates, which are the memory this design removes, and it has no rule for a smooth union across a root. |
| Run-time code generation (the external survey's note on Fidget) | A specialized method per program. | The tree is AOT-compatible by default. |
