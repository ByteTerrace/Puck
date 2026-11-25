# Mathematics in place of brute force

The owner's ruling (AGENTS.md, "Brute force is a defect when mathematics
answers the question directly"): where a `Puck.Maths` primitive, a generator's
jump-ahead, a bound or a paired value answers a question directly, the engine
uses it instead of repeating work. This plan carries that ruling through the
engine's largest brute-force costs, starting with the mesh bake: a cold bake of
the debug world made about 1.1 billion field samples and spent another 0.55
billion walks of the field on four-tap gradient stencils (the bake's done line,
`samples=` against `taps=`, which counted a gradient sample as five walks).

## 1. Gradients from one forward-mode walk

The bake's gradient lattices walk the field once in forward mode, a value and
its analytic gradient from one interpretation; [gradients and
normals](../rendering/sdf/reference/gradients-and-normals.md#the-bakes-gradients)
describes the mechanism, and `SdfFieldDeviceLawTests.Gradients` holds it to the
stencil on both backends. What remains:

**Still on the stencil.** The wallpaper fold, a masked relief (its mask weight
varies along the surface and the dual reads it as a constant) and lane erosion.
The bake's start line names the program's ops that fall back
(`stencil-ops=[…]`); with `none`, every gradient sample is one walk.

**Counts.** The done line's `taps=` counts work units:
`SdfMeshBakeRequest.GradientCost` (two) for a gradient sample, one for any
other, so a cold bake's `taps` falls by three times its gradient samples
(`kinds=[…gradient:…,crease:…]`). Walks of the field are `samples=` plus four
for each gradient sample whose program the start line lists ops for.

**Readings owed (the lead's quiet window).** A cold bake of the debug world on
each backend, `puck_world_start` with a fresh `cacheDir`, `paused` and
`lockstep`, then `world.wait bakes`: compare `samples`, `taps`, `kinds` and
`stencil-ops` against the base, and `puck mesh audit --world worlds/debug`
against the base's audit. Expected: `taps` falls by about three times the
gradient and crease samples; the audit's normal runs read the same or better
(the dual takes one face at a crease where the stencil blended two). What would
refute it: a group whose audit's normal error rises where its start line names
no stencil op.

**Considered and left.** A GPU count of the fallback walks
(`sdfWorkGradients` is counted, but the bake disables its counter row) needs the
bake's counter buffer zeroed and copied with each batch's readback; the start
line's static list answers the same question per program.

## 2. Bounds that skip a grid lattice's empty blocks

Built on intervals rather than on the 1-Lipschitz premise the first design
needed: the group's own CPU field encloses every value its fixed-point
evaluator answers over a box (`SdfFieldEvaluator.TryDistanceBounds`), so no
step scale enters the proof. Both grid lattices a group samples are bounded
once, on the bake's pool, before they are dispatched
(`SdfMeshPathBake.QueueBounds`, `SdfMeshPathBake.Bounds.cs`): a coarse search's
lattice and a final lattice before its first slab. The lattice is cut into
blocks of four samples a side; a box of blocks is halved along its longest run
until its enclosure clears the band (three cell diagonals) or it is one block.
A cleared block is never sampled: the host writes each of its samples the
enclosure's nearer end. Every other block is sampled as a block lattice
(`SdfMeshBakeRequest.Blocks`, lattice mode 1 in
`passes/sdf-mesh-bake-stage.hlsli`), whose answer the host expands
(`LatticeBlocks.Expand`).

**Invariant.** No lattice cell with a corner in a skipped block holds a zero,
and every skipped sample reads at least the band from zero with its true sign.
Every reader of a lattice judges a sample near the surface only by a small
value or a sign change across a cell (the coarse narrowing, the crossed cells,
their rings, the sheet and gap tests), so it judges the lattice exactly as it
judges the fully sampled one. What enforces it: `Classify`, the one function
that skips, whose only skip is an enclosure excluding `[-band, band]` over the
block's samples widened by one cell on every side and padded past every float
position the kernel computes. Where it stops: the enclosure is the CPU field's,
so a group whose GPU field the CPU cannot read (glyph thickening, a sampled
snapshot, slot lanes, an op the render reading refuses) samples every block;
the band absorbs the two readers' rounding apart.

**Counts (a CPU bake of the moth, `puck mesh audit --cpu --prototype moth`).**
The meshes are identical: 2,891,322 triangles, 1,601,542 vertices and the same
defect counts before and after. Samples fall from 66,499,882 to 64,618,835
(refine 10,614,483 to 9,541,504, slab 7,153,063 to 6,344,995) for 536,331
interval walks. The base's kinds show where a bake's interpretations go:
`crossing` 18,158,154, `fine` 14,485,788, `refine` 10,614,483, `material`
7,966,505, `slab` 7,153,063, `gradient` 7,192,748. Two thirds of the work lies
at the surface, where no bound can skip a sample, so the bounds cannot reach
the success line alone. What it did next, and what is left:

- **Crossings (built).** Each crossed edge's root is a safeguarded Newton
  iteration over dual trials (`SdfSampledLattice.CrossingRoot`; the [mesh-bake
  reference](../../.claude/skills/rendering/references/mesh-bake.md#contouring)
  holds the rules). The trial nearest the root carries the crossing's gradient,
  so the gradient stage no longer measures searched crossings. A CPU bake of the
  Moth: `crossing` 18,158,154 value samples to 10,730,752 dual ones, `gradient`
  7,192,748 to 4,020,633, the audit's defects the same or fewer. The CPU answers
  gradients from fixed-point taps whose slopes rarely pass the quadratic check,
  so it takes about 3.5 trials a root where a float field takes 1.5 to 2
  (`ACrossingLandsWithinASixteenThousandthOfItsEdgeInAFewRounds`); the GPU's dual
  walk is expected to read near the latter, about 6 M crossing samples on the
  Moth.
- **Material rounds (built, by another route).** A crossing's material does not
  stand for any point the cut samples (a quad's quarters and its vertices lie
  off the edges, lifted out of the surface), so reading the winner in the
  crossing walk would cost every dual walk material tracking for no point it
  covers. The field decides instead: a group whose geometry closes over one
  material samples no point, and a multi-material group's first round skips
  every point whose box its CPU field proves single-material
  (`SdfFieldEvaluator.TryUniformMaterial`). The Moth's `material` falls from
  7,966,505 to 4,689,996, most of it single-material groups; the boxes decide
  little on the Moth (46,852 points for 51,660 walks), so a group stops walking
  once its walks stop paying, which keeps the same 46,852 points for 1,941
  walks.
- **Fine samples (judged not to pay at the proved band).** Of the Moth's
  14,230,347 fine samples, 2,385,755 read more than three fine-cell diagonals
  from the surface and 8,121,298 more than one (a counted run on this branch).
  The grid bounds' band, three diagonals, could skip at most the first figure,
  less what an enclosure overstates, about 2 % of the bake for an interval walk
  per fine block. A one-diagonal band could reach several times that, but a
  measured cell of a coarser stride interpolates its stride cells' corners, so
  a skipped corner's filled value would move an interpolated crossing unless
  the widening is the stride's, which leaves little to skip. The structural
  answer is a sparse final lattice: blocks proved off the surface at the fine
  spacing are never laid out, so the coarse narrowing, the slabs and the
  refinement become one adaptive descent. It is the next step, sized by these
  counts.

A cold debug-world CPU bake does not fit a shared machine's memory (the host
account defers admission past five gigabytes of machine limit), so the
debug-world reading is the lead's: a cold windowed bake on each backend, reading
`kinds=[…]` and `bound=[lattices,refused,walks,skipped]` on the done line.

**Considered and left.** Narrowing the coarse search by bounds instead of
sampling it: the CPU enclosures widen through rotations and blends, so the
narrowed boxes stood looser and the final lattices coarser (the moth's
triangles fell from 2,891,322 to 1,111,350 with the same meshes planned). The
coarse search keeps its samples, which the bounds now thin.

## 3. Generation that jumps (audited)

The engine's seeded streams already position by jumping rather than stepping:
a generator site opens its stream at `skip + cursor` with
`Pcg32XshRr.Advance` (`GeneratorEngine.DrawStream.TryOpen`,
`GeneratorEngine.TryPositionExtended`), and the presentation's per-body draws
create a stream keyed by what they draw for (`WorldCreationInstances.CueRest`,
`VoiceBabbler`). The one serial loop over a shared stream left is a site's batch
(`GeneratorEngine.TryRunBatch`), and it is serial by meaning: a draw without
replacement narrows the masks the next draw reads, and a rejection-sampled draw
consumes a data-dependent number of words, so no partition knows its starting
position before the draws before it run. No change.

## 4. The sweep

**Pairing functions and their combined operations** (`UnsignedNumberFunctions`:
`ElegantSwap`, `ElegantTranslate`, `ElegantScale`, `ElegantSum`,
`ElegantDifference`, `ElegantMinimum`, `ElegantMaximum`, on a Szudzik-paired
value without unpairing). Judged not to win anywhere in the engine today:

- each combined operation starts from the pair's integer square root
  (`ElegantMaximum` is `SquareRoot`, a hardware square root and a correction for
  64 bits), where a packed pair's components (`PackedIntPair`) are a shift and a
  mask, so a hot loop over composite keys (the collapse's refusal map keyed by
  `PackedIntPairComparer`, the bake's lattice keys) pays more, not less;
- the order a Szudzik index sorts in, by the larger component and then along
  its shell, is the order no engine loop iterates in, and a deterministic total
  order over pairs is already the packed pair's lexicographic one;
- the index's density (every pair below n fits below n²) shortens a pair of
  small integers written as one LEB128 varint, and no store writes such a pair:
  the one LEB128 writer outside `Puck.Maths` writes single values
  (`CanonicalBinaryWriterExtensions`).

Where they belong: the expression language's `pair` and `unpair`
(`ExpressionArithmetic.Functions`) reach `ElegantPair` and `ElegantUnpair` over
zigzag-encoded components, so only `ElegantSwap` keeps its meaning there; a
signed component's sum or extremum is not the encoded components'. A world that
wants paired-cell arithmetic gets it as expression functions over signed pairs,
built when one asks.

**Already in place.** The bake's lattice indexing divides through
`ReciprocalDivisor32` (`SdfSampledLattice.FineCoordinates`), and the irradiance
volume and temporal history draw their sample sets from the low-discrepancy
sequences in `Puck.Maths` (`SdfMeshIrradiance`, `SdfTemporalHistory`).

## 5. One lowering over a carrier

The formula tree lowers by carrier, not by number system: the fixed-point,
wide and dual readers are the same tree rendered over a carrier's types and
functions. Each carrier is a rename of the fixed reader's text where its
operations keep the fixed carriers' names (the wide reader over `Wide`,
`Wide3`), or the dual library's functions where a node's derivative needs a
rule (the dual reader, whose activity analysis keeps an inactive operand as
the plain reader's text). The dual reader now has two targets from one
emitter (`SdfFormula.EmitAllDual`, `EmitAllFixedDual`): the bake kernel's
`SdfDual` and the query evaluator's `FixedGradient` (Puck.Maths), whose
functions `FixedGradients` names as the kernels' `sdfd` library does.

**Chosen over a generic method per formula.** A formula generic over
operator interfaces would be one emitted body, but its guards differ per
carrier (a lane mask selects, an interval joins both branches, a bool
returns), a constant operand would lift into the carrier on every call, and
the JIT's specialisation is the only thing between it and the fixed reader's
code. The rename keeps every carrier's code monomorphic and its constants
plain, and costs one emitter branch per carrier class.

**Built.** Contact gradients take one walk: `TryFieldGradient` walks the
point's region tape (a regional evaluator) or the stream under the point walk's
instance cull (any other) once over `FixedGradient`
(`SdfFieldEvaluator.Gradient.cs`) and normalizes the exact gradient of the
branch the point reads; `sdf.queries.gradient-walks` counts them. The power at
a constant exponent (the superellipsoid) and the lattice noise's octave sum
(`Pcg3dLatticeNoise.Fbm3` over `FixedGradient3`, so an unmasked relief) have
fixed-point duals. A walk that meets a warp, a masked relief, a hand-written
shape or a node with no fixed-point dual (a varying exponent, the ellipse's
root, the cell noise) answers by the six taps as before. A headless debug-world
boot (`world.wait 30`, `world.counters sdf.queries`) reads 4,338 gradients as
4,338 walks and no taps, where the base read them as 26,028 taps.

**Next, by the same mechanism.**

- An interval carrier: the same tree over `FixedInterval`, its guards joining
  both branches where the condition is undecided, in place of the hand-written
  shape rules in `SdfFieldEvaluator.Bounds.cs`; adopt per shape where its
  enclosure is at least as tight (the frame and the region tapes read it).
- A wide dual: `TrySurfaceNormal`'s six double taps (`TryWideGradient`) become
  one walk over a wide gradient carrier.
- Degree-two jets (`MonogenicAlgebra`, `x³ = 0`) for curvature in the bake's
  simplification error and adaptive detail.
- The physics solves that need f and f′ (`FixedSupportDistance`, a sweep's
  time of impact), the motion programs whose velocities difference poses, and
  rig effectors' Jacobian columns: each a dual walk once a count shows the
  differencing on its path. `FixedGyroscopicStep` already holds its Jacobian.
- The bake's CPU-side differences (`SdfMeshPathBake.FieldLevels.cs`) read
  through the same reader once the render reading's warps have duals.

## 6. Leftovers and the rest of the sweep

Done: spherical Fibonacci directions, the radical inverse and the golden angle
live in `LowDiscrepancy`; the golden angle reaches the kernels through the
generated `Shared/low-discrepancy.hlsli`.

Open: the wallpaper fold's, masked reliefs' and lane erosion's duals (their
derivatives are rules the dual library does not yet hold); the superellipsoid
law's infinities on both backends (`SdfFieldDeviceLawTests.ShapeFormulas`, a
GPU leg); the emulators' pixel composition.

Judged, each by its count:

- **The wide fixed-point lowering for batch queries.** A headless debug-world
  boot reads 24,214 point samples and 4,338 gradients over its first 35 ticks
  (`world.counters sdf.queries`), each a contact or probe query at its own
  point on its own region tape. Wide lanes pay where many points share one
  tape and one walk; no engine path batches queries that way, and the one that
  does (the CPU bake behind `puck mesh audit --cpu`) is a tool. It is built when
  a batch consumer exists, as one more carrier of section 5's emitter.
- **Structure of arrays for the body step and fact derivation.** Fact
  derivation already visits candidate slots, not the population
  (`WorldRuleHost.DeriveFacts` through `CollectFactCandidateSlots`, counted by
  `WorldTickWork.FactSlots`), and the body step is a branchy per-body program
  run in parallel kit partitions (`WorldBody.StepIsLocal`,
  `WorldPopulation.Partitions.cs`). Lanes pay for uniform arithmetic over many
  bodies, which neither loop is; a population of thousands is where a count
  would show otherwise, and the city stress world is where to take it.
- **The audio mixer's integer lanes.** At most 64 emitters (`AudioSnapshot`'s
  `DefaultMaxEmitters`) mix 48,000 frames a second into two channels, 6.1 M
  multiply-adds a second at the worst, well under a percent of one core. Each
  product is a 64-bit one (an `int` sample times a Q16 gain ramped in Q32,
  `AudioMixer.AccumulateMono`), and AVX2 has no signed 64-bit lane multiply, so
  `Vector<int>` lanes would widen and emulate it for no measurable gain.
- **The emulators' pixel composition.** One machine composes 38,400 pixels a
  frame (`AgbPpu.Composite`: window mask, the top two layers by priority, the
  blend), about 2.3 M a second, a few percent of a core per running machine.
  It matters at an MMO's thousands of machines, and a vector compositor must
  reproduce every pixel the Post batteries hold, so it is the gaming-bricks
  area's work with its batteries, not this sweep's.

## 7. The toolbox, structure by structure

Each structure the sweep weighed, at the hot path where it would pay most:
used (with its count), already in place, judged not to pay (with the reason),
or open (with what decides it).

| Structure | Hot path weighed | Verdict |
|---|---|---|
| Intervals | The bake's grid lattices | Used (step 2): the moth's samples fall from 66,499,882 to 64,618,835 for 536,331 walks, meshes identical. |
| Intervals | The bake's material points | Used (step 2): a box's region-tape walk proves one material (`SdfFieldEvaluator.TryUniformMaterial`); with the single-material groups the Moth's `material` falls from 7,966,505 to 4,689,996. |
| Intervals | The refinement's fine samples | Judged not to pay at the proved band (step 2): 2,385,755 of the Moth's 14,230,347 fine samples lie past three diagonals; a sparse final lattice is the next step. |
| Intervals | Physics broad phase and sweeps | Already in place: the contact solver reads the field's enclosure over a body's box (`FixedFieldContactSolver`'s `IFieldBounds`, joined with the moving solids' through `FieldBoundsUnion`), and a regional evaluator's box queries cost what stands near the box. |
| Intervals | Render culling of empty or solid tiles | Judged not to pay: the frame draws baked meshes, whose host boxes already cull per draw (`SdfMeshCull`), and no frame pass walks the field. |
| Intervals | Residency and far-product planning | Judged not to pay: a far product holds a settled residency's static draws, whose boxes are their baked meshes' bounds, so no box there overstates its content for an interval to tighten. |
| Intervals | Rules over ranges | Judged not to pay today: no rule the state system runs evaluates a field over a range; one that does takes the interval carrier of section 5. |
| Duals | Contact gradients | Used (step 5): a debug-world boot reads 4,338 gradients as 4,338 walks and no taps, where it read 26,028 taps. |
| Duals | The bake's crossings | Used (step 2): a safeguarded Newton search over dual trials whose nearest trial carries the crossing's gradient; a CPU bake of the Moth reads `crossing` 18,158,154 value samples as 10,730,752 dual ones and `gradient` 7,192,748 as 4,020,633. |
| Duals and jets | Wide normals, physics solves, motion velocities, rig Jacobians, curvature | Open (step 5's list), each by the count of its differencing. |
| Peano and successor arithmetic | Forward differencing along lattice rows | Judged not to pay: a lattice sample walks the whole program, whose terms are not polynomials of the sample's index; differencing would hold only for a single affine shape. |
| Space-filling orders | Probe, bake-block and dispatch order | Already in place where locality pays: the irradiance worker orders probes by Morton cell within a band (`SdfMeshIrradiance.CompareSpatial`, over `BinaryIntegerFunctions`' interleave), and `HilbertCurve` serves the state system. The bounds' blocks are classified by halving boxes, which is already local; a block lattice's order inside one dispatch does not move a count. A Peano curve is not added: no consumer here. |
| Complex numbers and rotors | Yaw, spawn rings, twist, oscillators | Already in place for the synth (`VoiceSynth`'s `FixedComplex` rotor, one multiply a sample). Judged not to pay for a body's yaw (`WorldBody.Step`, one `SinCos` a step) or a spawn ring (`WorldPopulation.Step`, one a spawn): one fixed-point `SinCos` per event, and storing yaw as a unit complex would change the state's format for no counted gain. A twist's per-sample `SinCos` in the field (`WarpPoint`) varies with the point, so no successive angle steps it. |
| Numeration and quasi-periodic sequences | Which rated views, captures and bodies update on which frame | Open: `SdfUpdateRates` holds a tier's items to one period; whether they share a frame or spread over it is printed (`world.rates`), and a Beatty or Sturmian phase per item pays only where a tier's items bunch, which a reading shows. |
| Rationals and continued fractions | Tick-to-frame ratios | Already in place: the fixed-step pump accumulates whole engine ticks (`FixedStepPump`), and the machines pace by `RationalRateAccumulator`. |
| Finite fields and transforms | Many normalizations, transfer redundancy, convolutions | Judged not to pay today: no engine loop normalizes many vectors at once (`BatchInverseKernel` pays from tens of inverses in one batch); `ReedSolomon` already backs the asset store; the mixer's filters are short recursive ones, so an NTT or FFT convolution has no long kernel to replace. |
| Exact geometry | Quadratic integers, hexagonal coordinates | Already in place in the state system (`HexagonalCoordinate` boards); no field or physics path carries a √2 or √3 relation it re-corrects. |
| Wide lanes and structure of arrays | A wide fixed-point lowering, the body step's and fact derivation's layouts, the mixer's and emulators' loops | Judged in section 6, each by its count: no batch query consumer, candidate-driven facts and branchy body programs, a mixer under a percent of a core whose products are 64-bit; the emulators' composition is open for the gaming-bricks area. |
