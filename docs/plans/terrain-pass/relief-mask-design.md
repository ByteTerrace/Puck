# The relief mask as an operand of its relief

A relief mask scales a creation's noise relief down inside regions the author
draws with shapes, so a lake's bowl or a building pad stands where its shapes
put it ([the terrain plan's gap 4](../terrain-pass.md#the-relief-mask)). It is
built as a short field scope in the instruction stream. Two adversarial
reviews of that form found 21 defects between them, and the second review
found ten after a round of fixes that was meant to close the first eleven.
This memo reads the instruction as built, says why patching it does not
converge, and designs the form that replaces it. It is a design. Nothing here
is built.

## Summary

- **The decision.** A mask stops being instructions. It becomes an operand of
  the relief instruction it weights: a compiled record in a side table of the
  program, as a path's edges and a convex polygon's vertices already are. The
  record holds a band and a list of captured shapes, each in the relief's own
  frame. One function on the CPU and one in the kernel turn it into a weight.
- **Why.** The 21 findings come from three properties of the built form. The
  region is spelled in the surface's vocabulary (shapes, scopes, point
  operations), so every reader of the stream must tell a region from a
  surface; 23 hand-written source files name the mask today. The weight
  travels between instructions in a register, so every rule about order,
  ownership and inherited state is a check somebody must remember. And each
  reader derives the band's numbers for itself. A record that is not in the
  stream removes the first two, and compiling it once removes the third.
- **What an author sees.** Nothing changes in a document. Two things widen: a
  plane may be a region shape, and a band keeps its width under any stamp
  scale without the emitter rescaling it. One thing narrows: a band's start
  and width snap to a grid of 2⁻¹² creation units, and a width below one step
  is refused by name.
- **The invariants.** Seven, each with one enforcing place
  ([the table](#the-invariants-and-what-enforces-each)). Four become
  structural: a type that cannot express the defect, or a derived value no
  caller supplies. Three stay partly conventional, and
  [the memo says which](#what-stays-review-only).
- **Two defects are not about masks.** The relief's step bound is charged in
  the wrong units under a scaled stamp, and a later shape in the same chain
  can take the charge. Both are in the tree for unmasked reliefs today, and
  the first two migration steps fix them before the construct changes.
- **What it costs.** One more call site of the kernel's shape dispatch, which
  this lane could not measure, and one rebake of every stored mesh, which the
  kernel edit causes whatever the form. A GPU leg for the lead's window
  measures the first ([step 5](#5-kernel-agreement-and-kernel-cost)).
- **What a prototype settled.** A numerical model confirmed the proposed step
  bound on the reviews' own counterexamples and on 400 random cases, showed
  the built bound failing 50 of them, and fixed two details of the design: the
  band's grid and the rule for skipping a far region
  ([what ran](#what-the-prototype-settled)).

## What is built, and why patching it does not converge

A relieved creation emits its shapes inside one field scope, then its relief.
Each mask adds a scope between them (`SdfProgramBuilder.BeginReliefMask`,
`SdfProgramBuilder.EndReliefMask`):

```text
PushField                          the creation's scope
  shapes …
  PushField  seeded at the band's end     one mask
    the region's shapes, hard union
    ReliefWeight                          reads the accumulator as the region's distance, multiplies
                                          a register by the weight, leaves the far distance
  PopField   Union                        composes that far distance into the parent
  NoiseDisplace                           multiplies its displacement by the register
PopField
```

The region's shapes are ordinary `SdfOp.ShapeBlend` instructions under
ordinary point operations. The scope is an ordinary `SdfOp.PushField` whose
Shape lane carries a seed. The weight is a per-sample register in both
interpreters, multiplied by `SdfOp.ReliefWeight` and consumed by the next
`SdfOp.NoiseDisplace`.

The first review found eleven defects in that form and the second found ten
more after `SdfProgram.RequireReliefMasks` was added as one admission door.
Sorted by cause rather than by symptom, the 21 findings are five things:

| Cause | What goes wrong | Findings |
|---|---|---|
| A region shape is a `ShapeBlend`, so every reader of the stream must ask whether a shape is a surface | Skinning counts a region shape as a skin owner; the audit gives it a slot; classifiers read its material and its unbounded reach | Second review: skinning. First review: member extraction, audit excerpts, surface classifiers |
| A region's scope is a `PushField` and `PopField`, so the mask takes part in composition | The pop computes a minimum against the far distance and is not a restore; a scaled pop lowers the field | Second review: the pop. First review: the scaled pop |
| The weight and the point are state carried between instructions | A mask inherits a fold from before its first shape; the weight crosses an instance boundary; the relief's charge lands on whichever shape closes the chain | Second review: inherited fold, stolen step bound. First review: absorbed step bound, pending weight, repeat seam |
| The region's vocabulary is whatever the stream admits, less a list of exclusions | A sampled region is admitted though it jumps and the CPU cannot read it; a hand-packed scale lane is trusted; a plane passes one validator and fails the other | Second review: sampled region, packed scale, plane. First review: detail shapes, repeat seam |
| Numbers are derived per reader | Two endpoints rounded independently collapse a band; a reciprocal rounds to zero; the seed is checked apart from the band; the step bound mixes creation and world units; bake and contact size their samples from the noise alone | Second review: collapsed band, scaled noise, narrow bands. First review: reciprocal, seed, narrow bands |

The admission door closed the cases its author pictured. It cannot close the
causes, because each cause is a property of the form. A check that walks the
stream can refuse a `Repeat` inside a mask and still miss the same `Repeat`
one instruction before it, which is the second review's inherited-fold
finding. A table of which instructions are mask instructions
(`SdfProgram.ReliefMaskInstructions`) helps a reader that consults it, and the
influence walk did not: `SdfFieldEvaluator.WalkPoints` takes a skin owner for
every shape. A third
round of the same kind would add checks for the ten new findings and leave the
next reader, the next opcode and the next numeric edge as exposed as these
were. [An area that stops converging](../../../.claude/skills/orchestration/SKILL.md#no-bad-loops)
changes its approach, so the form changes.

## The construct

### What an author writes

The document is unchanged: `noise.masks` holds entries of `shapes`, `fade`,
`start`, `inside` and `outside`
(`CreationReliefMaskDocument`, [the plan's table](../terrain-pass.md#what-an-author-writes)).
Everything is in the creation's units and frame. Three behaviours differ from
the built form:

- **A plane is a region shape.** "No relief below this height" is one plane.
  The built form refuses it only because a plane in the stream made the whole
  creation read as unbounded; a region that is not in the stream has no such
  effect.
- **The band is in creation units at every stamp scale.** The emitter no
  longer multiplies `start` and `fade` by the stamp's scale, because the
  weight is evaluated in the creation's frame, where the band already is what
  the author wrote.
- **The band snaps to a grid.** `start` and `fade` round to the nearest
  multiple of 2⁻¹² creation units (about a quarter of a millimetre at scale
  one), both must lie within 2¹² units, and a `fade` that rounds to zero is
  refused by name. `inside` and `outside` round to multiples of 2⁻¹⁶. The
  canonical document stores the snapped values, so it states the band the
  engine evaluates. [The band's carrier](#the-bands-carrier) explains the
  grid.

### The compiled mask

Three types hold a mask after the document. Each has one way to be made.

- **A band** (a new `SdfReliefBand`): the start, the width and the two
  weights, held as integers on the grids above. Its constructor takes the
  author's four numbers, snaps them, and answers either a band or a refusal
  that names the value. It gives the band's largest slope,
  `1.5 · |outside − inside| / width`, which is the only place that number is
  computed.
- **A region shape** (a new `SdfReliefMaskShape`): a pose, an optional
  elongation, and one captured shape. The captured shape is the record a paint
  already uses, `SdfPaintShape`: one primitive packed exactly as the field
  packs it, with the residual scale and turn its emission rode. It is made by
  running the one primitive emission, `SdfSolidGeometry.AppendScaledPrimitive`,
  into a fresh builder and reading the result back
  (`SdfPaintShape.Of`, `SdfProgramBuilder.CapturePaintShape`). The mask reuses
  that record and that capture. Since three features then share it (paints,
  authored shading's shape operation and relief masks), it is renamed to a
  neutral `SdfCapturedShape`.
- **A mask** (a new `SdfReliefMask`): one band and at least one region shape,
  plus two values derived when it is made: the sphere outside which the weight
  is certainly `outside`, and the box the band lies in.

The door is the pair of constructors that make a region shape and a mask.
Nothing else can make either value. The canonicalizer calls the door to
validate a document, and the stamp emitter calls it to build the program, so
the two cannot disagree about what a mask may hold. The door refuses, in one
place and with one message each:

- a captured shape whose type is not on a closed list (below);
- a Sweep, a revolved prism and a regular-polygon or path profile, the three
  refusals `CreationCanonicalizer.ValidateReliefMask` spells today, moved here
  unchanged;
- a non-finite or degenerate pose, scale or elongation.

The closed list is the captured shape types that are continuous, 1-Lipschitz
and read by the fixed-point evaluator: Box, Capsule, Sphere, Torus, Cylinder,
Plane, Vesica, RoundedRectangle, Ellipse, Trapezoid, ChamferedRectangle,
RoundCone, Superellipsoid and ConvexPolygon. It is written as a `switch` over
`SdfShapeType` with no default that admits, so a shape type added later is
refused until someone lists it. A sampled region, a glyph, a path and a sweep
are not on it.

What the types cannot express matters as much as what they hold. A region
shape has no blend, no smooth radius, no material, no detail flag, no dynamic
slot, no fold, no warp and no scope. Its distance correction is the smallest
component of the captured shape's residual scale, computed where the record is
made, never read from a lane a caller filled.

### The weight

At a point `q` in the relief's frame (the point the relief samples its noise
at), one region shape reads:

```text
u = turn_back(q − origin)               its pose
u = u − clamp(u, −elongate, elongate)   its elongation, when it has one
v = shape_turn_back(u / scale)          the captured shape's residual scale, then its turn
d = shape(v) · min(scale)               the one shape dispatch, kept a lower bound
```

A mask's region distance is `m = min(end, d₁, …, dₙ)`, where `end` is the
band's end, `start + width`. Its weight is

```text
w = outside                                        where m ≥ end
w = inside                                         where m ≤ start
w = inside + (outside − inside) · t² · (3 − 2t)    otherwise, t = (m − start) / width
```

and the relief adds `amplitude · (w₁ · … · wₖ) · noise(frequency · q)` to the
field. A relief with no mask multiplies by nothing, so its field is today's to
the bit, and so is a masked relief's wherever every weight is exactly one.
Where the product is exactly zero the noise is not evaluated at all, which
saves the octave loop inside every region of `inside: 0`, in the kernel and in
contact.

The region is evaluated at the relief's own point, after the relief's whole
point chain. A creation's relief chain is the stamp's prefix
(`CreationStampEmitter.EmitNoise`): the dynamic slot, the stamp's translation,
rotation and scale, and its mirror. So a mirrored, scaled or slot-carried copy
carries its masks with no work in the mask: the mask is the same record for
every stamp of a document, compiled once.

### Where it lives in a program

`SdfProgramBuilder.NoiseDisplace` takes the relief's masks as an argument. The
program keeps them in a side table keyed by the relief instruction's index, as
it keeps a path's edges and a polygon's vertices (`SdfProgram.SideTableValues`).
The packed relief instruction names its entry in two lanes that are zero
today, the y and z lanes of its second data vector, as whole-number floats, so
every lane of the instruction stays a finite float. The CPU never decodes that
reference; it reads the host table, as it reads a polygon's vertices.
`TerrainRelief`, the plan's successor to `NoiseDisplace`, takes the same
argument and the same table.

The packed entry is the band's four numbers, the mask's skip sphere, and per
region shape eight vectors: origin and shape type, pose rotation, elongation
and distance correction, residual scale, the captured shape's own rotation,
the shape's skip sphere, and the shape's two data vectors. A convex polygon's
vertices follow. The layout mirrors a paint's rows (`SdfAppearance.PackPaint`).

Three things read the stream by instruction and must carry a relief's masks
with it: the geometry key (`SdfMeshGeometryBuilder`), which hashes a side
table's values in place of its offset; `SdfProgram.Excerpt`, which copies the
entries of the instructions it keeps; and packing. Today each of them lists
the table-bearing shape types by hand. The design moves that knowledge into
`SdfProgram` as one registry: one accessor answers an instruction's table
values (the existing `SideTableValues`, extended to a relief), and a sibling
answers the instruction with its reference lanes cleared. The three readers
call both for every instruction, whatever its opcode. A mask edit then changes
the geometry key because the key cannot be computed without the table, and the
next table added to a program is carried the same way. This is the one place
where the built form is stronger than a side table: an instruction is keyed
for free. The registry is what pays for that.

### The readers

Two functions compute the weight.

- **The kernel.** A new function beside the noise in `field/`, called from
  `mapCore`'s `SDF_OP_NOISE_DISPLACE` arm before the octave loop. It loops
  over the masks and their shapes with no opcode dispatch and calls
  `evaluateShape`, as `sdfApplyPaints` and `sdfShadeShape` do.
- **The fixed-point evaluator.** One method of `SdfFieldEvaluator` for a
  point and one for a box. The point rule calls the evaluator's existing
  per-operation helpers in the fixed order above, on shapes compiled through
  the evaluator's one instruction compile. The box rule bounds each shape's
  distance with the existing shape bounds, takes the minimum, and reads the
  weight at both ends of the interval, since the weight is monotone in the
  region's distance. A shape whose bounds are unbounded gives the hull of
  `inside` and `outside`.

`SdfFieldEvaluator.WalkPoints`, `WalkBounds` and `mapCore` lose their relief
register, their seeded push and their `ReliefWeight` arm. Their
`NoiseDisplace` arms gain one call. No other reader of a program changes,
because none of them can see a mask.

### The band's carrier

The second review's collapsed-band finding is a band whose two endpoints round
to the same fixed-point number while the kernel still sees a band in float. The cause
is two carriers each rounding the author's floats their own way.

A band on a 2⁻¹² grid with every value below 2¹² in magnitude needs at most 24
bits, so single-precision float holds it exactly, and Q48.16 holds it exactly
because its step is finer. The kernel's `end − start` is exact for the same
reason. Both readers therefore compute with the same rational numbers, and a
band that exists in one exists in the other. The weights sit on a 2⁻¹⁶ grid,
which both carriers also hold exactly. Neither reader stores a reciprocal; each
divides by the width.

What remains different between the readers is the region's distance, which
each computes in its own arithmetic, as it does for every shape. The weight
differs by the band's slope times that difference
([the prototype's reading](#what-the-prototype-settled)).

### The step bound

The marcher and the bake need a bound on how fast the field changes. The
relief adds `g(p) = A · W(q) · N(f · q)`, where `q` is the relief's point as a
function of the sample point `p`, `A` is the packed amplitude, `W` is the
product of the weights and `N` is the normalized noise, at most one in
magnitude. Its gradient is at most

```text
|A| · J · ( f · G · Γ  +  Σ slopeₖ )
```

where `J` bounds how much the point chain stretches a derivative, `G · Γ` is
the noise's own gradient bound per unit of `q` (the constants
`SdfProgram.NoiseDisplaceStepFactor` holds), and `slopeₖ` is each band's
largest slope. The sum over masks is sound because every weight is at most
one. Everything inside the parentheses is in the relief's frame, so one
multiplication by `J` converts noise and masks together. For a stamp of scale
`s` the packed amplitude is `a · s` and `J` is `1 / s`: the bound is
`a · (f · G · Γ + Σ slope)`, the same at every scale, which is what
`CreationNoiseDocument.StepFactor` already reports to an author.

Two rules make the analysis compute this, and neither is new to the code:

- **A field operation is charged at its own instruction.** It adds its bound
  to the accumulator it edits. `SdfProgram.AnalyzeLipschitz` does this for
  cellular relief today. For a noise relief it does so only when the relief's
  chain holds no shape, and otherwise multiplies the factor into the chain's
  candidate, which a later shape of the chain then carries into a `max`. That
  is the stolen step bound. The relief moves to the cellular rule in every
  chain.
- **The point factor comes from the chain before the instruction.**
  `SdfProgram.AnalyzeCellPointFactors` computes it for cellular relief: one
  over the smallest axis of each `Scale`, times the factor of each warp that
  has a global bound, and a refusal by name when the chain holds an operation
  with no global continuous derivative bound. The noise relief reads the same
  table. That supplies the `1 / s` whose absence is the wrong-units finding.

A masked relief takes the cellular rule whole, including its refusal: a fold
or a reach-dependent warp before a masked relief is refused by name. This
turns away nothing that could be written before the mask existed. An unmasked
relief gains the scale term and the per-instruction charge but keeps today's
admission, because a `puck.sdf.v1` document can already write a relief after
a cell fold (`SdfDocumentDecoder`), and refusing that is a decision this memo
does not take ([open questions](#open-questions-and-their-owners)).

### Skipping a far mask exactly

Far from a region the weight is `outside` and nothing needs evaluating. The
skip must be exact: if skipping a shape could change the weight, the weight
would jump on the skip's boundary, and no slope bound covers a jump.

A shape may be skipped where its distance is certainly at least `end`. Some
admitted shapes read less than the true distance: an ellipsoid's field grows
only `min(r) / max(r)` as fast as distance along its long axis. The program
already certifies this ratio per shape as a distance gain
(`SdfProgram.InstanceDistanceGain` and the per-shape rule behind it, which
answers one for an exact primitive, less for a gauge, and zero for a shape
with no certificate). So a shape's skip sphere is its bounding sphere grown by
`end / gain`, widened by the slack the program's other bound spheres carry,
and a shape with no positive gain is never skipped. The gain is the captured
shape's, times the smallest over the largest axis of its residual scale, and
the bounding sphere includes the elongation. A mask's skip sphere encloses its
shapes'. Both are computed where the mask is made.

The prototype shows what the gain is for: on the lake basin's shape, a skip at
the bounding sphere plus `end` moves the weight by 0.69 at the sphere, and a
skip at the bounding sphere plus `end / gain` moves it by nothing. The built
form avoids this for a superellipsoid because the program gives that shape no
bound sphere at all. Whether its per-shape skip is exact for a region shape
under a non-uniform `Scale` was not checked.

### What bake and contact read

A band narrower than the bake's coarse cell or the collider's gradient taps is
invisible to both today: each sizes its samples from the noise's wavelength
alone. The compiled mask carries the two facts they need, the band's width and
the box the band lies in, and both consumers read them instead of deriving
anything from the document.

- **The bake.** `WorldShapeDetail.PartsOf` declares one more part per mask
  whose band is finer than the relief's own spacing: the band's box clipped to
  the relieved surface's, at a quarter of the band's width. The part is
  flagged so that every cell the surface can enter refines, not only the cells
  whose coarse samples show the surface turning. The lattice has that rule
  already for glyphs, whose field hides features between samples
  (`SdfLatticeRegion.Bound`); the flag is new only on `SdfInstancePart`, which
  carries it there.
- **Contact.** `WorldColliderSet.ReliefField` takes its gradient step as the
  smaller of what it computes today and a quarter of the narrowest band.

## The invariants and what enforces each

| Invariant | The one thing that enforces it | Where that stops |
|---|---|---|
| The weight lies in [0, 1], so a masked relief reaches no farther than its amplitude | `SdfReliefBand`'s constructor: weights are 16-bit fractions, and no other code makes a band | Nowhere. Every reach that reads the relief's amplitude stays sound unchanged |
| The weight is continuous, with slope at most the band's in the relief's frame | The types: a region shape has no field that could hold a fold, a warp, a blend or a scope; its shape type comes from a closed list; its correction and skip sphere are derived where it is made; and its relief's chain passes the point-factor rule | Each listed shape's own 1-Lipschitz property is that shape's, held by `SdfPrimitiveLipschitzLawTests`. The exact skip is arithmetic at an edge and stays review-only |
| The relief's step bound includes the band's slope in the right units | One formula (`SdfProgram.NoiseDisplaceStepFactor`), fed one point factor per instruction, added at the relief's own instruction. Noise and bands are in one frame, so no caller converts units | An unmasked relief after a fold or a reach-dependent warp keeps today's charge, which is not certified |
| A mask composes nothing into the field | Structure: a mask is not an instruction. There is no accumulator write, push or pop to get wrong, and `SdfOp.ReliefWeight` is deleted | Nowhere |
| CPU and GPU read the same field | One table with two readers; a band both carriers hold exactly; one member order fixed by the record; one vocabulary list; `SdfEncodingProbe` passing every packed field | The two readers agree by convention, as every instruction's do. This stays review-only, with the kernel-agreement leg as its check |
| Bake and contact see the band | The compiled mask's width and box, which the two consumers read | Nothing forces a future consumer to read them. The bake's sample budget and the collider's gradient floor still bound how narrow a band either resolves |
| A mask is part of its relief's identity: every key and excerpt that carries the relief carries its masks | The side-table registry in `SdfProgram`, which the geometry key, the excerpt and packing call for every instruction | A reader that bypasses the registry and hashes raw instruction words. The geometry key is the only reader this lane found that hashes instructions apart from the program's packed words |

## What stays review-only

[The hazards page](../../development/hazards.md#silent-invariants) lists the
classes that break without a symptom. This design leaves three of them open
and names them so the implementing change gets an adversarial review of
exactly these:

1. **The packed entry's encoder and decoder.** The C# packer, the kernel
   function and the fixed-point reader agree on the entry's rows by
   convention, like the rest of the instruction set.
2. **The exact skip.** A skip that claims a skipped shape could not change the
   weight needs counterexample tests at the arithmetic edge: points one raw
   either side of each skip sphere, for every listed shape type under a
   non-uniform scale.
3. **Consumers of the band's size.** That the bake and the collider read the
   band is a fact about two call sites.

## Where the guarantee stops

- **A region's distance is its shapes' field, which may be less than true
  distance.** A band around a long ellipsoid is at least `fade` wide
  everywhere and wider along the long axis. The built form behaves the same.
  The slope bound holds either way, because it needs only a 1-Lipschitz field.
- **Wherever `inside` is zero** the relief's term is exactly zero on and
  inside the region, bit-exact in the fixed-point field and within float
  rounding in the bake's samples, as [the plan states](../terrain-pass.md#what-stays-exact).
- **The shading octaves** the terrain's materials will read are not masked by
  this design. Because a mask is now the kind of record a paint is, the
  shading stage can evaluate the same mask at the hit as it evaluates a paint,
  with no per-vertex weight from the bake. The terrain plan's shading step
  owns that choice.

## The second review's findings, answered

| Finding | Holds on the tree? | What closes it | Step |
|---|---|---|---|
| A mask inherits a discontinuous fold from point operations before its first shape (R1) | Yes: admission restricts the instructions after the seeded push and never requires a reset before the first shape | A region shape's pose starts from the relief's point and is the whole of its frame; the type has no fold. The relief's own chain is checked by the point-factor rule | 3 |
| A later shape takes an earlier relief's step-bound charge (R2) | Yes: the analysis asks whether the whole chain holds a shape | The relief is charged at its own instruction in every chain | 2 |
| Independently rounded endpoints collapse a valid float band on the CPU (R3) | Yes: the evaluator's compile rounds each lane on its own | The band is one value on a grid both carriers hold exactly; a width below one step is refused | 3 |
| Admission accepts a sampled region, which jumps and which the CPU cannot read (R4) | Yes: admission excludes a plane and a detail shape and nothing else | The closed list of shape types, with no default that admits | 3 |
| A hand-packed scale lane makes the region steeper than its bound (R5) | Yes, and for surface shapes too: packed admission never compares a `Scale`'s correction with its axes | For regions, the correction is derived where the record is made. For the stream, packed admission re-derives every host-baked lane the analyses trust | 1, 3 |
| Skinning consumes an owner at each mask shape and refuses the skin (R6) | Yes: the influence walk takes an owner per shape instruction | No mask shape is an instruction, so the owner list and the walk count the same shapes | 3 |
| A canonical mask pop still changes the saved field (R7) | Yes, for a parent above the far distance | There is no pop | 3 |
| A scaled creation's noise derivative is in the wrong units (R8) | Yes, and without any mask: the charge multiplies a world amplitude by a creation frequency | The point factor of the relief's chain | 2 |
| A plane mask passes document validation and fails at field build (R9) | Yes | One door answers both callers, and it admits a plane | 3 |
| Bake refinement and contact normals miss a narrow band (R10) | Yes; the missed contour is unconfirmed at runtime | The mask's width and box, read by the parts declaration and the collider | 4 |

## The first review's findings, answered

The round of fixes closed some of these and the second review reopened
others. The answer here is against the new construct, not the fixes.

| Finding | What became of it | What closes it |
|---|---|---|
| A mask chain absorbs its relief's step bound (M1) | Reopened as R2 | The per-instruction charge |
| Packed admission accepts a seed that cuts the band short, and a seed can overflow (M2) | Closed by deriving the seed from the band | There is no seed and no rescale by a region bound: `end` is the band's own number, and the region's distance is 1-Lipschitz by construction |
| An admitted pop lowers the field and invalidates the instance cull (M3) | Reopened as R7 | There is no pop, and the cull has no mask scope to step over |
| Member extraction gives mask shapes a slot and can build an empty scope (M4) | Closed by a table of mask instructions | No mask instruction exists to drop or keep; the excerpt carries a relief's masks through the registry |
| A pending weight crosses an instance boundary (M5) | Closed by an owner check | No weight is pending between instructions: a relief owns its masks |
| A repeat seam puts a jump in the weight (M6) | Reopened as R1 | The types and the point-factor rule |
| Detail shapes make the CPU and GPU masks disagree (M7) | Closed by a refusal | A region shape has no detail flag |
| The CPU rounds the mask's reciprocal to zero (M8) | Reopened as R3 | No reader stores a reciprocal; both divide by an exact width |
| Bake refinement and contact sampling ignore the mask's feature size (M9) | Still open, as R10 | Step 4 |
| Audit excerpts lose point transforms a mask left behind (M10) | Closed by tracking a mask's point operations | A mask moves no point |
| Surface classifiers count mask materials and unbounded geometry (M11) | Closed by a shared reading; the plane half reopened as R9 | Classifiers read shapes, and a region shape is not one |

## Options weighed

| Option | What it is | What it closes | Why not, or why |
|---|---|---|---|
| Patch the scope a third time | A check per finding in `RequireReliefMasks` and its partners | The ten findings as stated | The five causes stay. Each new reader, opcode or numeric edge is exposed as these were |
| Keep the mask in the stream under its own opcodes | A begin and an end that save and restore all walk state, and a region-shape opcode that shares the surface's arm | Surface classification, by the opcode. Composition, by restoring | Chains, segments, instance owners, slot tracking and excerpts are still computed over every instruction, so each of those analyses still owes the mask a rule |
| A region sub-program in a second instruction list | The region's instructions, interpreted by a restricted interpreter in each reader | Everything the chosen form closes | Three small interpreters that must each refuse the same opcodes and mirror the main arms. A record with a fixed order needs no dispatch and cannot hold an opcode |
| **An operand in a side table, as typed records** | This memo | All 21 findings | It adds a side table that keys and excerpts must carry (the registry) and one more call site of the shape dispatch |
| A sampled weight lattice | Bake the weight to a grid and sample it | Continuity, by interpolation | A second representation of a region, a resolution that must beat the band, and a fixed-point sampler for contact. The plan already leaves painted masks for later |

The chosen form is not borrowed from another engine. It follows from two
things Puck already has: paints, which evaluate a captured shape for coverage
at a hit, and program side tables. A mask is a paint's sibling that the field
reads instead of the shading stage.

## What the prototype settled

A numerical model of the masked relief, written in Python with numpy and run
in this lane without the engine, answered four questions. It is scratch and is
not kept in the tree; its output is in the commit that adds this memo. The
model is a sphere with value-noise relief (quintic interpolation of corner
values in [−1, 1], normalized octaves), masked by ellipsoid-gauge regions with
smoothstep bands, under a uniform stamp scale.

1. **Is a band on the grid the same number in both carriers?** Two million
   random grid values below 2¹² converted to float32 and to Q48.16 with no
   mismatch. The second review's collapsing band rounds to a width of zero
   steps, which the band's constructor refuses. *Decided:* the 2⁻¹² grid and
   the 2¹² limit.
2. **Does the proposed step bound hold where the built one fails?** The
   model reproduces the second review's two numbers: the stolen charge reads a
   slope of 1.406 against a built bound of 1.375 and a proposed 2.875; the
   scaled stamp reads 1.765 against a built 1.400 and a proposed 2.999. Over
   400 random cases (stamp scales from 1/64 to 64, up to three masks, up to
   four octaves, 60,000 gradient samples each) no sampled slope exceeded the
   proposed bound, and the largest reached 0.984 of it. The built formula was
   exceeded in 50 cases, by up to 3.7 times. *Decided:* the formula in
   [the step bound](#the-step-bound), with the sum over masks kept.
3. **Is a skip exact?** On an ellipsoid of radii 46, 17 and 46 with a band 8
   wide, a skip at the bounding sphere plus the band's end changed the weight
   by up to 0.689. A skip at the bounding sphere plus the end over the gain
   changed it by nothing in 400,000 points. *Decided:* skip spheres use the
   certified gain.
4. **How far apart do the two carriers read one band?** For widths from 8
   down to 2⁻⁸ units, a float32 and a Q48.16 smoothstep at the same region
   distance differed by at most 3.4 · 10⁻⁵, about two steps of the weight's
   grid. *Decided:* no stored reciprocal and no tolerance beyond the shapes'
   own.

The model does not settle the kernel's cost or the real evaluator's rounding
at a skip sphere. Steps 3 and 5 own those.

## Migration steps

Each step is one change that leaves the tree working, with the check that
shows it did. Steps 1 and 2 fix defects that exist without any mask and do not
depend on the rest.

### 1. Packed admission re-derives the lanes the analyses trust

`SdfProgram.ValidatePackedContract` refuses a `Scale` whose correction lane is
not the smallest of its three magnitudes, or whose magnitudes fall under the
builder's floor (`SdfProgramBuilder.Scale` bakes both). It refuses a `Rotate`
whose quaternion, and a `SymmetryPlane` or plane shape whose normal, is not of
unit length within float rounding, since the step analysis counts each as an
isometry.

*Check:* the second review's stream (a region sphere under a scale of two with
the correction lane set to 32), handed to `SdfProgram.Of`, is refused by name.
`Puck.SignedDistance` builds and the debug world boots headless, so every
program the emitters produce still passes.

### 2. A relief is charged at its own instruction, through its chain's point factor

`AnalyzeCellPointFactors` becomes the point factor of every field operation
that samples the point: cellular, noise and sine relief. `AnalyzeLipschitz`
adds each one's bound at its own instruction in every chain; the chain-product
path for `NoiseDisplace` and `Displace` and its shape-free test go. Where the
chain before an unmasked noise or sine relief holds an operation the
point-factor analysis cannot bound, the term keeps the factor today's analysis
gives that chain, so nothing that builds today is refused. The mask slopes the
noise relief adds are still the built table's until step 3.

*Check:* two cases through the real analysis and the evaluator's render
reading, each comparing the recorded bound with gradients sampled near the
surface: the five masked reliefs before a later shape, and the sphere stamped
at 1/64. Both recorded bounds now exceed every sample. A third case stamps one
relieved sphere at scales 1, 64 and 1/64 and reads the scope's recorded bound
(`SdfProgram.FieldScopeClamps`): it equals `CreationNoiseDocument.StepFactor`
for the document at all three, where today it does so only at scale one.

### 3. The construct

One change, because no half of it stands alone (rule 5: no second form
alongside):

- the three types and the door; `SdfPaintShape` renamed and shared;
- the side table, its registry, packing, and the encoding probe's call;
- the kernel function and the evaluator's point and box rules;
- `NoiseDisplace` taking masks; `CreationStampEmitter.EmitNoise` compiling a
  document's masks once and passing them, with no scale applied;
- the canonicalizer validating through the door and storing snapped bands;
- the masked relief's chain under the point-factor rule's refusal;
- everything in [what it deletes](#what-it-deletes);
- the plan's lowering and exactness sections, the
  [sync pair](../../../.claude/skills/rendering/references/sync-pairs.md#instruction-semantics-shared-by-both-interpreters)
  and the authoring skill rewritten to the new form in the same change.

*Checks:*

- `puck search` for the deleted names in `src/` and `tests/` finds nothing.
- `SdfReliefMaskLawTests`, retargeted, holds the two properties it holds
  today (outside every band the masked field is the unmasked field bit for
  bit; on and inside a region of zero inside weight the relief is exactly
  zero) and gains one case per closed finding: the fold before a masked
  relief is refused; the collapsing band is refused; the door refuses a
  sampled region and a path by name, and has no parameter that could carry a
  detail flag or a fold; two smooth-unioned slots under one masked relief
  still skin with two owners; a plane region builds, and its point and box
  rules agree.
- One case per listed shape type under a non-uniform scale evaluates the
  weight with skipping on and off at points one raw either side of the skip
  sphere and reads the same bits.
- A mask edit that changes no instruction word changes the group's geometry
  key.
- The debug world boots headless, and the north-west sector's recorded scope
  bound is what it was after step 2.
  `puck mesh audit --cpu --world worlds/debug --prototype valley-north-west`
  shows no new open edges or slivers.

### 4. Bake and contact read the band

`SdfInstancePart` gains the flag that reaches `SdfLatticeRegion.Bound`;
`WorldShapeDetail.PartsOf` declares the band parts; `WorldColliderSet.ReliefField`
takes the band into its gradient step.

*Check:* the reviews' case, built as a test prototype: a broad flat box with
one slow octave, relief suppressed in a 2 cm strip with 5 mm fades between
coarse columns 10 cm apart. `puck mesh audit --cpu` reads the baked surface
inside the strip at the unrelieved height within one fine cell, and the
collider's normal at a fade's midpoint is within a few degrees of the
analytic one. If the lattice's sample budget cannot reach the strip, the
audit shows by how much, and the step says so in its commit.

### 5. Kernel agreement and kernel cost

A GPU leg for the lead's batched window. It needs a windowed World on both
backends and is the terrain plan's kernel-agreement step applied to the new
form.

- *Agreement:* `puck mesh audit` of the masked north-west sector against the
  kernel bake, on Direct3D 12 and Vulkan, within the bake's tolerance.
- *Cost:* the bake and views kernels' bytecode size and compile time before
  and after step 3, and the benchmark route's frame statistics. *Decides:*
  whether the mask's reader keeps calling the whole shape dispatch. If a
  kernel grows past what its compile or its register budget tolerates, the
  reader calls a dispatch restricted to the closed list, generated from the
  same C# list so the two cannot differ.
- *Looks:* the plan's windowed captures of the lake and the gallery's RELIEF
  MASK exhibit, which must read as their plaques say.

### 6. One reader for a captured shape

`sdfApplyPaints` and `sdfShadeShape` each unpack a captured shape and apply
its scale, turn and correction in their own lines, and the mask's reader would
be a third copy. The three move to one kernel function over one packed block,
written by one C# packer.

*Check:* the views kernels' `[pipeline-statistics]` register counts are no
higher, and the gallery's paint exhibits read as their plaques say in the same
window as step 5.

## What it deletes

- `SdfOp.ReliefWeight`, its generated define, its rows in `SdfOpRoles` and the
  evaluator's supported set, and its arms in `mapCore`, the point walk, the
  box walk, the cull and the cell analysis.
- The seeded push: `SdfProgram.IsReliefMaskPush`, the seed read in `mapCore`,
  and the seed branches of the evaluator's compile and both walks.
- The whole of `SdfProgram.ReliefMasks.cs`: `RequireReliefMasks` and its band
  check, `ReliefMaskInstructions`, both `IsSurfaceShape` overloads and their
  cache, the slope table and the endpoint writer. The slope formula moves into
  the band.
- From `AnalyzeLipschitz`: the `ReliefWeight` arm that rewrites the weight's
  and the push's words, the push table kept for it, the mask-slope plumbing
  through the chain pass, and the shape-free branch the noise and sine reliefs
  share.
- `SdfProgramBuilder.BeginReliefMask`, `SdfProgramBuilder.EndReliefMask` and
  the builder's open-mask state.
- `SdfFieldEvaluator.ReliefMaskWeight` as an instruction arm, the relief
  register in both walks and in `mapCore`, and the cull's step over a mask
  scope (`SdfFieldEvaluator.TryCertifyInstance`).
- The mask tables in `SdfMeshAudit.ShapeFields`, `SdfMeshAudit.SlotWalk` and
  the audit's own-part extraction, and the `IsSurfaceShape` calls in the bake
  group's drawn test and the glass classifier.
- The mask loop of `CreationStampEmitter.EmitNoise`, with its per-stamp
  scaling of the band, and the three type refusals in
  `CreationCanonicalizer.ValidateReliefMask`, which the door now answers.

## Systems considered

- **Field scopes** (`SdfProgramBuilder.PushField`): the built form. Rejected
  for the mask as above; unchanged for everything else.
- **Paints and the captured shape** (`SdfPaintShape`, `SdfPaint`,
  `sdfApplyPaints`): reused as the region shape's record and capture. A paint
  already is a shape evaluated for coverage over a smoothstep band, outside
  the field.
- **Authored shading's shape operation** (`sdfShadeShape`): the second reader
  of the same record, and the reason step 6 exists.
- **The rigid-leaf plan** (`SdfProgram.CompileRigidPlan`): a compiled pose
  plus a tight bound sphere, walked with no opcode dispatch. It is the model
  for the mask's packed rows and skip test. It cannot be the mask's record,
  because a leaf names a shape instruction in the stream and carries no scale
  or elongation.
- **Program side tables** (`SdfProgram.SideTableValues`, `SdfCompiledPath`):
  reused, and generalized into the registry.
- **The cellular relief's point factors** (`SdfProgram.AnalyzeCellPointFactors`):
  reused for the noise relief. It already holds the scale term and the
  refusal the reviews asked for.
- **Distance gain** (`SdfProgram.InstanceDistanceGain`): reused for the exact
  skip.
- **The lattice's bound regions** (`SdfLatticeRegion.Bound`): reused for the
  band's bake part.
- **Fixed-point smoothstep** (`FixedQ4816.Smoothstep`): reused unchanged.
- **The lane-erosion pending value** (`SdfProgramBuilder.LaneErode`): the
  consume-and-clear register the built mask copied. Left as it is: its value
  is consumed by the very next shape in the same chain, and no finding names
  it.
- **Sampled fields** (`SdfProgramBuilder.SampledValues`): a painted mask.
  Still left for later, as the plan says.

## Found beside the mask

These are in the tree today and are not caused by the mask. Each has an owner.

- **The sine relief shares the stolen-charge path.** `Displace` in a chain
  that holds a shape multiplies its factor into the chain's candidate, which
  joins the accumulator by a maximum. When the accumulator's bound already
  exceeds the candidate's, the relief's term is lost. This is read from the
  analysis; no witness was run. A creation's sine relief is emitted in a chain
  of its own with no shape, so authored content takes the sound path today.
  *Owner:* step 2, which moves `Displace` with the noise relief.
- **The smallest axis of a scale is computed in several places.** The step
  analysis, the cell point factors and the paint packer each spell it.
  *Owner:* step 2, which touches two of them, moves it to one.
- **A comment names a gradient walk that does not exist.** The rigid plan's
  header says it compiles for `mapCore`'s and `mapGradCore`'s walks; no
  shader declares the second. *Owner:* step 3, in passing.

## Open questions and their owners

- **Should an unmasked relief after a fold be refused, as cellular relief
  is?** Noise read through a fold jumps at every seam, so no step bound
  covers it, and the analysis has no term for those jumps. A
  `puck.sdf.v1` document can write it today. Refusing it removes something an
  addon can write; keeping it leaves a field the marcher cannot bound. This
  memo changes neither. *Owner:* the lead, as a question for the owner, since
  it is about what the engine should accept.
- **Does the kernel tolerate a fourth site of the shape dispatch?** *Owner:*
  step 5, with the fallback named there.
- **Does the lattice's budget reach a band of a few millimetres?** *Owner:*
  step 4, whose check reports the shortfall if there is one.
