# Presentation field queries

Two presentation features ask the world's signed distance field where the
nearest surface is, every frame: the creation effectors' surface probes, which
plant feet (`WorldCreationInstances.ApplyEffectors`, through
`WorldSurfaceQuery.March`), and the chase camera's clearance sweeps
(`WorldCameraClearance`). Both read the fixed-point interpreter
(`SdfFieldEvaluator`) over one program that holds the whole static world.
[The CPU frame](open-items.md#the-cpu-frame), item 4, names two replacements:
batch the probes onto the GPU and answer them a frame late, or compile the CPU
reader. This page weighs them, records the design another model family
reviewed, answers that review finding by finding, and gives the order of the
work that remains.

These are presentation queries. Nothing here moves the simulation's
fixed-point contact.

## Summary

- **The choice is the CPU, not the GPU batch.** A GPU answer arrives more than
  a frame after the question, which shows where the field is discontinuous (a
  stair edge under a foot, a wall the camera swings into), makes every
  presentation query asynchronous, and still needs a CPU path on every host
  without a device.
- **The cost was the walk, not the arithmetic.** Every sample walked a program
  the size of the world: about 1,500 instruction visits and 1,900 bound tests,
  where what can change the field near a foot is a hundred or two
  instructions. The first part of the design removes that walk, and it is
  built: a regional evaluator keeps, for each cube of the world a query
  touches, the chains that can change the field there (`SdfFieldEvaluator`'s
  region index). Its answers are the plain evaluator's, to the bit.
- **The review changed the second part.** The first design pruned with a
  tolerance and a Lipschitz bound, and read the result in single precision.
  The review showed the Lipschitz bound does not hold per candidate, that a
  generated expression does not bound the disagreement between readers, and
  that an approximate sample breaks the march's contract. The pruning is now
  exact and rests on the interval interpreter and the cull's certificates,
  which already carry those proofs. The float reader keeps a narrower role: it
  never decides a hit, and its field semantics are lowered from the
  catalogue's shared definitions, not written a third time.
- **The fixed-point evaluator stays on the client.** It is the reference that
  makes every decision and records every region. Its retained bytes are their
  own item ([memory](open-items.md#memory-on-the-deck-tier)); this design
  does not remove them.

## What a query cost, and what it costs now

The counts come from a scratch program that builds the placement half of the
debug world's presentation field as `MotionClearance.TryCreate` builds it (the
solid placements `WorldPlacementStamper.Queryable` admits, emitted by
`WorldPlacementStamper.EmitStatic`), constructs a plain and a regional
`SdfFieldEvaluator` over it, and reads `SdfFieldEvaluator.PointWork`,
`SdfFieldEvaluator.RegionWork` and `SdfQueryWork.ProcessWork` around one query
at a time. They are counts of the world's documents, not timings. The live
field also holds screen slabs and neighbours' strips, which the scratch
program leaves out.

The program is 124,106 instructions in 17,601 instances. Its root is not a
plain union: 15,733 of its 17,511 root shapes join by smooth union, with radii
from 0.14 m to 0.40 m.

| Query | Plain evaluator | Regional evaluator, regions built |
|---|---|---|
| A foot probe (a 1.2 m ray, then the gradient at its hit): 8 point evaluations | about 11,700 instruction visits and 5,600 bound tests | about 900 to 1,200 instruction visits, no bound test |
| A 6 m camera sweep: 4 to 9 point evaluations | about 7,200 to 13,600 visits and 7,400 to 16,700 bound tests | about 1,000 to 1,500 visits |
| A 400 m pick ray: 57 point evaluations | about 111,000 visits and 106,000 bound tests | about 18,000 visits |
| Mean visits a sample over 13,442 scattered points | 1,960 | 229 |

The two evaluators agreed on distance and material at every one of those
points and on every query above.

The first time a query touches a part of the world it records the regions on
the way to its sample. A foot's first probe in a new place visited between
11,000 and 22,000 instructions to do so, about what two foot probes cost
before. A cold 400 m pick ray visited about 184,000, close to its plain cost.

What is left near the debug world's floor is real geometry: the leaf at the
origin holds 186 instructions, a dozen relieved terrain masses whose
certificates cannot rule them out of a 2 m cube. Three quarters of a foot
probe's evaluations are the six taps of its gradient.

## Why the CPU, not a GPU batch

| | GPU batch, answered late | CPU reader |
|---|---|---|
| When the answer arrives | More than a frame later, and by a varying amount: a readback is read when its fence signals (`ShaderPipelineRenderNode.PollReadbacks`), and background GPU work (`SdfGpuWork`) is paced by the frame's spare time. | In the call. |
| What the delay does | A foot swinging onto a stair reads the tread it left. A camera turned into a wall is inside it until the answer returns. | Nothing; the eye keeps the limit `WorldCameraClearance` states for every frame. |
| What callers become | Every query, and every authored program that asks one, is asked now and answered later. The camera's rise search, which bisects on the previous answer, must guess its candidates ahead or spread over frames. | Unchanged: ask, read, decide. |
| Which hardware pays | The GPU, the device the frame bar is read on. | CPU cores the frame leaves idle: the pose pack forks its registrations across the job pool. |
| Hosts without a device | Need a CPU path anyway (a headless host, tests, a server), so there are two implementations. | One path everywhere. |
| What it needs resident | A second program on the device (the static solids alone, which is what the queries read) and the interpreter kernel's pipeline, in a player's process that otherwise never bakes. | Nothing new on the device. |

The GPU option is real: the bake kernel already samples point lists with
gradients (`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-bake.comp.hlsl`'s
point mode). It loses on what a player sees. The decision register allows a
simulation tick pipelined beside the frame but not latency in what the view
shows of the hand
([the pipeline foundation](../decisions/rendering.md#the-pipeline-foundation)),
and a clearance that answers late is that latency.

At the scale Puck is for, every zone is a world and a client in a town holds
hundreds of feet over a field as large as the zone. A query's cost has to
follow the neighbourhood on every host that asks one. The server's contact
already has that shape: it keeps one field per collider and narrows them by a
grid (`FixedColliderGrid`), and a headless boot of the debug world reads about
60 instructions a point evaluation with no world-wide prepass
(`world.counters sdf.point`). Presentation was the reader that still walked
the world.

Emitting a specialized method per program at run time
([the external survey](field-programs/research-elsewhere.md) raises it, after
Fidget) is ruled out by the tree: every project is built AOT-compatible by
default (`Directory.Build.props`).

## Region tapes

This part is built. The lasting description is the header of
`SdfFieldEvaluator`'s region index (`SdfFieldEvaluator.Regions.cs`) and
[queries and determinism](../rendering/sdf/handbook/queries-and-determinism.md#region-tapes);
what follows is what the rest of the design leans on.

An evaluator constructed regional keeps, for each cube of the world a point
query has touched, a tape: the ranges of the instruction stream a walk at any
point of the cube has to read. Cubes halve from 16 m to 2 m, each recorded
over its parent's tape. A point walk reads the tape of the cube its point lies
in, with its own per-point skips still in force.

A tape is the record of one box walk over the cube. The walk holds the
interval that encloses, at each place in the stream, the accumulator a point
walk holds there at every point of the cube. That is the interval
interpreter's inclusion property, which `SdfFieldBoundsLawTests` holds for
every op, shape and blend. Against that interval the walk drops three things:

- an instance or a chain whose cull certificate clears the interval's upper
  end at every point of the cube (the point walk's own skip, asked at the
  cube's nearest point to the certificate's centre);
- a join it finds inert, because the candidate it enclosed over the cube
  stands past the accumulator's upper end by the margin at which the blend
  returns the accumulator to the bit: the whole chain when every join is
  inert, and otherwise the inert joins that stand before the chain's first
  join that is not;
- everything before a chain whose first join that is not inert takes the root
  accumulator over, because the candidate stands below the accumulator's lower
  end, and below the far seed a walk starts from, by the same margin. A chain
  that has joined by another blend, edited the field or crossed a scope takes
  nothing over ([the handbook](../rendering/sdf/handbook/queries-and-determinism.md#region-tapes)
  holds the argument).

Each of those leaves every point walk in the cube with the distance and
material it had. Nothing is dropped on a tolerance, a Lipschitz bound or a
value at one point, and the order of the stream is kept.

The third rule changes no tape on the debug world's field, whose stream order
puts near geometry early. It is what a program emitted in rows needs: beside
one shape of a grid of a thousand, a walk reads a few shapes with it and a few
dozen without (`SdfFieldEvaluatorRegionLawTests.ATapeHoldsASmallPartOfALargeStream`).

The presentation field (`WorldFramePresenter`'s static field), a routed
scene's dressed field (`WorldSessionSceneEmitter`) and the motion check's
clearance (`MotionClearance`) are regional. The server's fields are not: its
samples already cost what one collider holds.

## The float reader

This part is open, and the review reshaped it.

**What it is for.** After region tapes, what a sample costs near the floor is
the arithmetic of a hundred or two instructions in fixed point, and a foot
probe spends three quarters of its evaluations on the six taps of one
gradient. A reader in single precision and vector width answers those six
taps as one walk, and fills a lattice for the mesh audit and the CPU bake
eight points at a time. That is its payoff. It is smaller than the first
design claimed, because the walk it was to remove is already gone.

**What it may not do.** It never decides a hit, an overlap or a clearance.
The march's contract is that a sample is exact or is a value at or below the
exact one (`SdfFieldMarch`'s sampler), and a float answer is neither until
its error is certified. So:

1. Its first consumers take no decision from it: the gradient at a hit the
   fixed-point march has already accepted, and lattice samples for the audit
   and the bake, which are compared with the kernels in single precision
   anyway.
2. A march may take a float sample only as a bound, the role the banded
   evaluator's grid already plays (`SdfBandedFieldEvaluator`): the float value
   less a certified error, used to advance, with the fixed-point walk of the
   same tape wherever the march could accept or stop. That needs a per-tape
   error bound this design does not have yet, and it is the last step, taken
   only if the counters show marches still matter.

**Its semantics are lowered, not rewritten.** `SdfFormula` lowers each shared
shape and blend formula to fixed-point C# and to HLSL. The stream's own
semantics are not shared today: a fold, a field edit, the masked relief and a
scope's pop are each written once in `SdfFieldEvaluator` and once in the
kernels, with the catalogue only choosing the dispatch. The review found one
where the two read differently: a morph pop reads its lane in the kernels and
a fixed zero in the evaluator, a convention no shared definition states. So
the order is:

1. Move the state transitions into shared definitions in the catalogue: a
   point op as a map of the point and the distance scale, a field edit as a
   map of the accumulator, a pop as a blend with its lane value an input.
   Lower the two existing readers from them. This step pays without a third
   reader: it removes the hand-synced pairs and settles the morph pop.
2. Add the vector lowering. A condition becomes a mask and an early return a
   select. The table and loop shapes (the convex polygon, the sweep, the
   path, the ellipse's projection) take an algorithm definition lowered the
   same way, or stay with the fixed-point reader, which then answers their
   chains for the float reader.

**Precision.** The reference is the fixed-point reader, not the kernels. The
review's example shows why: a small shape behind two large translations loses
its offset in the kernels' single-precision accumulation and keeps it in
fixed point, so the two existing readers already differ by millimetres there.
The float reader therefore takes each chain's rigid prefix from the
fixed-point reader's compiled instructions, composes it exactly, and rebases
it to its region's centre before narrowing to single precision. Its
agreement with the fixed-point reader is stated on a domain (a region's
extent, a fold's phase clear of its seam by a stated margin, the lanes'
packed ranges) and checked by a law over that domain. Off that domain the
chain is the fixed-point reader's.

**The gradient keeps its stencil.** The six-tap central difference fits one
vector of eight. The four-tap tetrahedral stencil the first design proposed
reads zero at a corner where the central one reads the corner's diagonal, and
a foot's standoff moves by centimetres with it.

## Invariants

| Invariant | What enforces it | Where it stops |
|---|---|---|
| A regional evaluator answers what the plain one answers, to the bit, in distance and material. | A tape is made only by the regional box walk (`SdfFieldEvaluator`'s `WalkBounds` with a record), and each of its three drops is one predicate stated against the enclosing interval. `SdfFieldEvaluatorRegionLawTests` holds the two evaluators to each other on a fold across its seam, tied candidates, a staircase of smooth unions, relieved scopes and carves, and on the raws either side of every region face. | It is an exactness-preserving skip, the class [silent invariants](../development/hazards.md#silent-invariants) marks review-only: sampled agreement can miss an arithmetic edge. It rests on the interval interpreter's inclusion and on the cull certificates, as the plain evaluator's own skips do. |
| A sample reads the tape of the cube it lies in. | The lookup is the only way to a tape, and it descends by the point's own raws. Each tap of a gradient looks its own cube up. | None known. |
| The index is bounded, and scattered samples do not churn it. | It counts the cubes it marks and records and drops them all at its limit (`sdf.regions.resets`). A cube is recorded by the second sample that reaches it; the first marks it and reads the cube around it. | A sample that never comes back costs a plain walk. A workload that returns to more cubes than the limit rebuilds as it goes; a lattice consumer would need a larger limit or coarser leaves. |
| A float answer decides nothing. | To be enforced by its type when it is built: it offers a gradient and a lattice fill, and a march sampler only in the bound role with a certified error. | Not built. |
| A held answer is reused only over the field it was asked of. | `WorldFieldAnswer.Ask` compares the field instance, as before. | Unchanged. |

## Answers to the design review

The review read the first design. Each finding is answered here; the sections
above carry the changes.

**H1. The prune rule drops a live operand in a repeat chain, and the
Lipschitz bound does not certify a candidate over a cell.** Confirmed. A
limited repeat declares no Lipschitz action and no certificate
(`SdfFieldOps`' `RepeatLimited`), and a fold is not continuous across its
seam. The tolerance rule and the centre-plus-slope bound are withdrawn. A
region now keeps or drops a chain on the candidate's enclosure over the cube
by the interval rules (a repeat's through the evaluator's fold bounds), or on
a cull certificate, and keeps whatever neither can judge. The review's
counterexample is a law
(`SdfFieldEvaluatorRegionLawTests.AFoldedChainStaysInTheTapeOfARegionItsSeamCrosses`).

**H2. Generating the same expression does not enforce a distance tolerance.**
Confirmed, including the translation example: the rigid plan accumulates its
pose in single precision (`SdfProgram.CompileRigidPlan`). The float reader no
longer claims agreement with the kernels. Its reference is the fixed-point
reader, it takes its transforms from that reader's exact form and rebases
before narrowing, and its agreement is stated on a domain and checked there
([precision](#the-float-reader)). The fixed-point grid's half tick is part of
that budget.

**H3. An approximate sample cannot enter the march as exact or as a lower
bound.** Confirmed against `SdfFieldMarch`'s sampler contract. Region tapes
are exact, so their samples enter as exact and the march is untouched. The
float reader takes no decision; it may enter a march only in the bound role
with a certified error, which is the last and optional step.

**M1. The proof fails at the last drop.** Confirmed: the tape's accumulator
can still stand at the far seed when the last dropped operand acts, so the
difference the first proof bounded there is unbounded. The review's
corrected argument (the first join to take the accumulator below the
threshold is always retained, and the gap from there is at most one smooth
radius) is sound as far as I can check it. The design no longer needs it:
nothing is dropped on a tolerance, so there is no gap to bound and no budget
to split across runs and levels.

**M2. Rounded smooth minimum can amplify an error.** Confirmed as stated for
fixed point. It no longer bears on region tapes, which introduce no error to
amplify: a dropped join returns the accumulator to the bit by the margin the
cull already proves (`SdfFieldEvaluator`'s smooth-union identity margin), and a take-over returns the candidate to the bit by the same margin on the blend's other arm.

**M3. A hand-written vector walk is another implementation of field
semantics.** Accepted, and it is the larger correction to the float reader.
The stream's semantics move into shared definitions first, the two existing
readers are lowered from them, and the vector reader is a third lowering. The
morph pop's two readings become one definition in that step, with the lane
value an input the evaluator sets to zero where a static bake does.

**M4. Exact distance agreement does not preserve foot normals.** Confirmed:
at a corner of three planes the tetrahedral taps all read the same value. The
central six-tap stencil stays, on both readers.

**L1. The effective radius and the order of evaluation.** Both hold in what
is built. A region's margin floors the smooth radius as the blend floors it
(one function, used by the cull and by the region walk), and a tape lists its
ranges in stream order; nothing is sorted.

**Unconfirmed: the debug world's exposure.** The counts above are still of
the placement half built offline, and the page says so. The one-run claim
and the tape-size estimate they supported are gone with the tolerance rule.
What the live field's tapes hold is read from `world.counters sdf.regions` on
a windowed run, which is a verification request.

## Build order

Region tapes are built. What remains, each step with the check that shows it
worked:

1. **Windowed verification of region tapes, by the lead.** Feet on a slope
   and on the stairs (`debug.go stairs`) and the camera against walls read as
   before, which they must, since the answers are the plain evaluator's.
   `world.counters world.field-queries`, `sdf.point` and `sdf.regions` over
   the benchmark route give instructions a sample and regions recorded on the
   live field. `host.frames`' capture part before and after, from frozen
   builds, is the timing.
2. **Shared definitions for the stream's semantics.** *Check:* the generated
   fixed-point walk answers every law in `tests/Puck.SignedDistance.Tests` as
   the hand-written one does; `puck shaders generate --check` passes;
   `SdfFieldDeviceLawTests` gains the morph and stairs pops it leaves out
   today.
3. **The vector lowering, on its stated domain.** *Check:* a CPU law samples
   every admitted op, shape and blend through the vector reader against
   `SdfFieldEvaluator` inside the domain and shows the fall-back outside it.
4. **The gradient as one vector, and a lattice fill.** *Check:* a foot
   probe's point evaluations fall from eight to the march's two or three plus
   one vector walk (`sdf.point` beside the reader's own counter); the
   normals agree with the fixed-point stencil within the stated angle on the
   debug field; `puck mesh audit --cpu` passes from the float lattice.
5. **Float samples as march bounds.** Only if step 1's counters show marches
   still matter after step 4. It needs the per-tape error certificate.
   *Check:* a march's hits are those of the fixed-point march, as the banded
   evaluator's contract states, and exact samples a march fall.

Two things outside this lane would cut what a sample costs as much as the
float reader, and both stay exact:

- **Tighter certificates for relieved scopes.** A dozen relieved terrain
  masses stand in every leaf near the floor because a relief's scope carries
  a wide slack. *It would show:* the leaf at the debug world's origin holding
  fewer than its 186 instructions.
- **The evaluator's retained bytes.** The compiled instruction is the bulk of
  what the client's evaluator holds. *It would show:* `puck host heap` naming
  the tenant and its size.

## Systems considered

| System | What it does | How this design uses it |
|---|---|---|
| The interval interpreter (`SdfFieldEvaluator`'s box walk, `SdfFieldBoundsLawTests`) | Encloses the field over a box, rule by rule, with outward rounding. | It is what a region is recorded with. The first design passed over it for a Lipschitz bound; the review sent the design back to it. |
| The cull certificates (`SdfFieldEvaluator`'s reach certificates and segment hierarchy) | Prove, per sample, what cannot change that sample's answer. | A region asks the same question of every point of its cube at once. |
| `SdfDistanceGrid` and `SdfBandedFieldEvaluator` | A lattice of exact values, filled on first touch, that lets a march read a bound far from surfaces. | The contract a float sample would enter a march under. |
| `FixedColliderGrid` | The static colliders near a region, by their bounds. | Not used: bounds keep about 1,100 instructions near any foot of the debug world, because terrain bounds reach everywhere, and it lives in the physics layer. |
| `SdfProgram.Excerpt` | A program of some of another program's instructions. | Not used: it builds and validates a whole program per excerpt, where a tape is a list of ranges into one. |
| `SdfFormula` and `SdfFieldCatalogueGenerator` | One formula and one catalogue entry generating both readers. | Extended in the float reader's steps: the stream's semantics join the shared definitions, then a third lowering. |
| The two root-union certificates | "Every root compose is a hard union", computed once for the kernels' part walk and once for the evaluator's primer. | Folded into one (`SdfProgram`'s `ComposesByHardUnion`), read both ways. |
| The bake kernel's point mode and `SdfGpuWork` | Samples point lists on the GPU off the frame. | The GPU option, weighed above. |
| `WorldFieldAnswer` | Holds an answer on its owner and proves when it may be reused. | Kept as it is. |
| `GravityOctree` | Octree scaffolding for the gravity solvers. | Not shared: it partitions bodies for a solve rebuilt each tick, where regions are lazy, immutable and keyed by raws. |
| Run-time code generation | A specialized method per program. | Ruled out: the tree is AOT-compatible by default. |
