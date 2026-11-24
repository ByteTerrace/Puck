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

**Considered and left.** Newton crossings that take the gradient from the last
trial: each crossing round already straddles its estimate a tolerance apart
(`SdfSampledLattice.CrossingRoot`), so a root usually closes in two or three
rounds of two value walks; a Newton round costs one dual walk (two units) and
needs the same rounds to bound its root, and merging the gradient stage saves
one gradient sample a crossing. The saving is a few percent of a bake for a
search whose bracket guarantee would have to be rebuilt; it is not worth it
while the slabs below dominate. A GPU count of the fallback walks
(`sdfWorkGradients` is counted, but the bake disables its counter row) needs the
bake's counter buffer zeroed and copied with each batch's readback; the start
line's static list answers the same question per program.

## 2. Bounds that skip a final lattice's empty blocks (next)

A final lattice samples every point of its group's narrowed box
(`SdfMeshPathBake.SlabOf`), though most of a solid's box lies inside it or far
outside, where only a sample's sign is read. The coarse lattice the box was
narrowed from (`SdfMeshPathBake` refine stage, `CoarseSamples` a side) already
holds a value at every coarse point. Each shape's candidate in the walk is
multiplied by its step scale (`SDF_SHAPE_STEP_SCALE`); whether that makes the
whole sampled field 1-Lipschitz, through warps, reliefs and scopes, is
unverified, and the step's first check reads it against
`SdfProgram.AnalyzeLipschitz`.

**Design.** Keep the last coarse lattice's values with the group. Split the
final lattice into blocks of 8×8×8 samples. A block whose nearest coarse sample
c bounds it away from the surface, `|f(c)| - maxdist(c, block) > 3·cell`, is not
sampled: the host writes each of its samples `sign(f(c))·(|f(c)| - |s - c|)`, an
underestimate of its distance with the right sign. Every other block is
dispatched as a block lattice (the lattice mode the record leaves free, mode 1:
one point-table row a block, holding its origin, each expanding to 512 samples
in grid order), and its answer scatters into the final lattice's values and
materials.

**Invariant.** No cell the surface crosses has a skipped corner: a crossed
cell's corners lie within a cell's diagonal of a zero, and a skipped sample's
bound puts every zero at least three cells away. What enforces it: the one
function that decides a block's skip, with the margin, and the Lipschitz
property of the sampled field. Where it stops: a field whose step scale
understates its Lipschitz constant (an uncertified warp) would let the bound
skip a crossed block, so a group whose program holds an op with no certified
Lipschitz factor (`SdfProgram.AnalyzeLipschitz`) samples every block.

**Checks.** `puck mesh audit --cpu` on the debug world's groups before and after:
identical crossed-cell sets and topology (the skipped samples never feed a
crossed cell, a refinement or a sheet test), and the `kinds=[slab:…]` count
falling by the skipped share. The CPU answer (`MeshCpuBake.Answer`) answers block
lattices with the same expansion (`SdfMeshBakeRequest.SampleAt`).

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
