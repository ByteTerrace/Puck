# The relief mask as an operand of its relief

A relief mask scales a creation's noise relief down inside regions the author
draws with shapes, so a lake's bowl or a building pad stands where its shapes
put it ([the terrain plan's gap 4](../terrain-pass.md#the-relief-mask)). It is
built as a short field scope in the instruction stream. Two adversarial
reviews of that form found 21 defects between them, and the second review
found ten after a round of fixes that was meant to close the first eleven.
This memo reads the instruction as built, says why patching it does not
converge, and designs the form that replaces it. A design review of the
memo's first draft found six flaws in that design before it was cut short;
this revision closes each at its cause and
[answers them one by one](#the-design-reviews-findings-answered). Steps 1
and 2, which fix defects that exist without any mask, are built; the construct
itself (step 3 onward) is a design, parked for a step-back.

A second design review then found seven more flaws in this revision.
[The step back](#the-step-back), at the end of this memo, returns to what the
lake and the gallery need and recommends a smaller form of the mask. Where the
two differ, the step back is the current recommendation.

## Summary

- **The decision.** A mask stops being instructions. It becomes an operand of
  the relief instruction it weights: an immutable value that travels with
  that instruction, as a convex polygon's vertices travel with their shape.
  The value holds a band and a list of region shapes, each in the relief's own
  frame. One function on the CPU and one in the kernel turn it into a weight.
- **Why.** The 21 findings come from three properties of the built form. The
  region is spelled in the surface's vocabulary (shapes, scopes, point
  operations), so every reader of the stream must tell a region from a
  surface; 23 hand-written source files name the mask today. The weight
  travels between instructions in a register, so every rule about order,
  ownership and inherited state is a check somebody must remember. And each
  reader derives the band's numbers for itself. A value that is not in the
  stream removes the first two, and compiling it once removes the third.
- **What an author sees.** Nothing changes in a document. Two things widen: a
  plane may be a region shape, and a band keeps its width under any stamp
  scale without the emitter rescaling it. Two things narrow: a band's start,
  width and end snap to a grid of 2⁻¹² creation units within 2¹² units, and a
  band outside that is refused by name; and a relief takes at most 255 masks
  of at most 255 shapes each.
- **The invariants.** Eight, each with one enforcing place
  ([the table](#the-invariants-and-what-enforces-each)). Five become
  structural: a type that cannot express the defect, or a derived value no
  caller supplies. Three stay partly conventional, and
  [the memo says which](#what-stays-review-only).
- **Three defects are not about masks.** The relief's step bound is charged
  in the wrong units under a scaled stamp. A later shape in the same chain can
  take the charge. And the program's raw door trusts lanes the builder
  computes, such as a scale's distance correction and the noise's
  normalization. All three hit unmasked reliefs too, and the first two
  migration steps, now built, fixed them before the construct changes.
- **One ruling this rests on.** The lead ruled that an unmasked noise relief
  after a fold is refused, as cellular relief already is, because no step
  bound covers the jump at a fold's seam. A `puck.sdf.v1` document can write
  that today, so this is a refusal of something an addon could write.
- **What it costs.** One more call site of the kernel's shape dispatch, which
  this lane could not measure, and one rebake of every stored mesh, which the
  kernel edit causes whatever the form. A GPU leg for the lead's window
  measures the first ([step 6](#6-kernel-agreement-and-kernel-cost)).
- **What a prototype settled.** A numerical model confirmed the proposed step
  bound on the reviews' own counterexamples and on 400 random cases, showed
  the built bound failing 50 of them, and reproduced each of the design
  review's six counterexamples against the first draft's rule and the revised
  one ([what ran](#what-the-prototype-settled)).

## What is built, and why patching it does not converge

A relieved creation emits its shapes inside one field scope, then its relief.
Each mask adds a scope between them (BeginReliefMask,
EndReliefMask):

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
interpreters, multiplied by the ReliefWeight op (since deleted) and consumed by the next
`SdfOp.NoiseDisplace`.

The first review found eleven defects in that form and the second found ten
more after RequireReliefMasks was added as one admission door.
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
(ReliefMaskInstructions) helps a reader that consults it, and the
influence walk did not: `SdfFieldEvaluator.WalkPoints` takes a skin owner for
every shape. A third round of the same kind would add checks for the ten new
findings and leave the next reader, the next opcode and the next numeric edge
as exposed as these were. [An area that stops converging](../../../.claude/skills/orchestration/SKILL.md#no-bad-loops)
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
  one). The snapped start, the snapped width and their sum, the band's end,
  must each lie within 2¹² units, and the width must be at least one step. A
  band outside that is refused by name. `inside` and `outside` round to
  multiples of 2⁻¹⁶. The canonical document stores the snapped values, so it
  states the band the engine evaluates.
  [The band's carrier](#the-bands-carrier) explains the grid.

### The compiled mask

Three types hold a mask after the document. Each is immutable and has one way
to be made.

- **A band** (a new `SdfReliefBand`). It stores four numbers: the start, the
  width and the two weights, as integers on the grids above. Its constructor
  takes the author's four numbers, snaps them, checks the start, the width and
  the end against the limit, and answers either a band or a refusal that names
  the value. The band gives its end, `start + width`, and its largest slope,
  `1.5 · |outside − inside| / width`. No other code computes either.
- **A region shape** (a new `SdfReliefMaskShape`). Its constructor takes what
  an author writes for a shape: the primitive, its scale, its facets (taper,
  profile, lift, rounding, chamfer, exponent), a pose and an optional
  elongation. It lowers the primitive itself, through the one primitive
  emission, `SdfSolidGeometry.AppendScaledPrimitive`, into a fresh builder,
  and keeps what that emitted: one packed primitive with the residual scale
  and turn its emission rode. A paint already captures a primitive this way
  (`SdfPaintShape.Of`, `SdfProgramBuilder.CapturePaintShape`), and the region
  shape reuses that capture. No caller hands the constructor a packed
  primitive, so no caller can supply a plane's normal, a correction or a
  bound.
- **A mask** (a new `SdfReliefMask`): one band and between one and 255 region
  shapes. A relief takes at most 255 masks.

The capture itself is closed in the same change. `SdfPaintShape` is a public
record today: any caller can construct one with its own data vectors, scale
and bound, or copy one with a lane replaced, and its side table is a list the
caller may still hold and change. It becomes a sealed type with no public
constructor, made only by the capture, and it owns a frozen copy of its side
table. Since three features then share it (paints, authored shading's shape
operation and relief masks), it is renamed to a neutral `SdfCapturedShape`.
Paints gain the same protection.

The region shape's constructor is the door. The canonicalizer calls it to
validate a document, and the stamp emitter calls it to build the program, so
the two cannot disagree about what a mask may hold. It refuses, in one place
and with one message each:

- on the author's specification, before lowering: a Sweep, a prism the author
  revolves (`lift: Revolve`), and a path profile. These are the refusals
  `CreationCanonicalizer.ValidateReliefMask` spells today, moved here
  unchanged;
- on what the lowering produced: a packed shape type that is not on a closed
  list (below);
- a non-finite or degenerate pose, scale or elongation.

The revolve refusal reads the specification because the packed form cannot
tell an authored revolve from a lowering's own. Three ordinary primitives
lower to a revolved profile and stay admitted: a cone, a chamfered cylinder
and a torus scaled alike in X and Z.

The closed list is the packed shape types that are continuous, 1-Lipschitz
and read by the fixed-point evaluator: Box, Capsule, Sphere, Torus, Cylinder,
Plane, Vesica, RoundedRectangle, Ellipse, Trapezoid, ChamferedRectangle,
RoundCone, Superellipsoid and ConvexPolygon. It is written as a `switch` over
`SdfShapeType` with no default that admits, so a shape type added later is
refused until someone lists it. A sampled region, a glyph, a path, a sweep and
a regular polygon are not on it.

What the types cannot express matters as much as what they hold. A region
shape has no blend, no smooth radius, no material, no detail flag, no dynamic
slot, no fold, no warp and no scope. Its distance correction is the smallest
component of the residual scale its own lowering emitted.

### The weight

At a point `q` in the relief's frame (the point the relief samples its noise
at), one region shape reads:

```text
u = turn_back(q − origin)               its pose
u = u − clamp(u, −elongate, elongate)   its elongation, when it has one
v = shape_turn_back(u / scale)          the captured shape's residual scale, then its turn
d = shape(v) · min(scale)               the one shape dispatch, kept a lower bound
```

A mask's region distance is `m`, the least of its shapes' distances. With the
band's four stored numbers and `end = start + width`, its weight is

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
carries its masks with no work in the mask: the mask is the same value for
every stamp of a document, compiled once.

### How a mask travels with its relief

`SdfProgramBuilder.NoiseDisplace` takes the relief's masks as an argument.
`TerrainRelief`, the plan's successor to `NoiseDisplace`, takes the same
argument.

A program holds four kinds of data beside its instructions today: a convex
polygon's vertices, a sweep's curve, a path's edges and a sampled binding.
Each sits in a list keyed by its instruction's index, and for the first three
the instruction holds the place the table was packed at
(`SdfProgram.SideTableValues`). That form has three weaknesses that an
optional operand would turn into silent defects:

- `SdfProgram.Excerpt` lets a caller rewrite one instruction into any number
  of instructions and gives the source's table entry to the last instruction
  emitted so far. A relief rewritten into two copies would leave the first
  with no masks. A relief rewritten into nothing would hand its masks to the
  instruction before it. For a polygon the existing admission turns both
  mistakes into a refusal, because a polygon must have vertices. A relief's
  masks are optional, so the same mistakes would pass.
- A copied instruction carries a packed place from another layout in its
  lanes.
- The geometry key (`SdfMeshGeometryBuilder`) hashes a table's values in
  place of its offset for a hand-written list of shape types. A table missing
  from that list is not in the key, and its edits bake nothing.

So the design changes the carrier for all five kinds at once, and it is its
own migration step:

- **An instruction and its operand are one value.** The program's constructor
  and `Excerpt`'s rewrite take and answer pairs: an instruction, and either no
  operand or one immutable operand of a closed set of kinds. A rewrite that
  answers a pair twice answers the operand twice. One that answers nothing
  drops it. No interface takes an instruction apart from its operand, and no
  index-keyed list is public.
- **The door checks the pairing.** A relief's masks ride only a relief
  instruction, and vertices only a convex polygon. A shape that needs an
  operand must have one.
- **A typed instruction holds no place.** The lanes that name where an operand
  was packed are zero in every typed instruction, and the door refuses a
  nonzero one. Packing writes the place into the packed words alone. A copied
  instruction therefore has nothing stale to carry.
- **The packed operand is self-relative.** A relief's operand packs as a
  header holding its mask count; per mask, the band's four numbers, the
  kernel's skip sphere and the shape count; per shape, eight vectors (origin
  and shape type, pose rotation, elongation and distance correction, residual
  scale, the captured shape's own rotation, the kernel's skip sphere, and the
  shape's two data vectors) and a polygon's vertex count; then the vertices.
  Every count is stored before what it counts, and every nested position is
  derived from the counts. The limits are part of the value: an operand,
  when a relief has one, holds 1 to 255 masks, a mask holds 1 to 255 shapes,
  and a polygon holds the vertex range `SdfPrismProfile.MaxConvexVertices`
  already bounds. Only the relief's packed reference is absolute: one
  unsigned word, the operand's offset in vectors, bounded by the program's
  extent. The kernel checks each range against that extent before reading it,
  as it checks a rigid plan's.
- **The key hashes the logical value.** For each pair the geometry key hashes
  the instruction and, when there is one, the operand's kind followed by its
  counts and values in the order above. No position is in what it hashes, so
  nothing has to be cleared first, and a kind the key has never heard of is
  still hashed, because the key asks the operand for its bytes.

The CPU never decodes a packed reference. It reads the operand beside the
instruction, as it reads a polygon's vertices today.

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
to the same fixed-point number while the kernel still sees a band in float.
The cause is two carriers each rounding the author's floats their own way.

A value on a 2⁻¹² grid with magnitude at most 2¹² needs at most 24 significant
bits, so single-precision float holds it exactly, and Q48.16 holds it exactly
because its step is finer. The band's constructor requires three values to be
such numbers: the start, the width and the end. The first draft checked only
the start and the width, and the design review showed a start one step below
the limit whose end rounds onto the limit in float, so the kernel took the
outside branch where Q48.16 read one half. With the end in the same domain,
the kernel's `start + width` is an exact float addition and equals the
fixed-point end. Both readers therefore compare the region's distance with the
same three rational numbers, and a band that exists in one exists in the
other. The weights sit on a 2⁻¹⁶ grid, which both carriers also hold exactly.
Neither reader stores a reciprocal; each divides by the width.

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

Three rules make the analysis compute this.

- **A field operation is charged at its own instruction.** It adds its bound
  to the accumulator it edits. `SdfProgram.AnalyzeLipschitz` does this for
  cellular relief today. For a noise relief it does so only when the relief's
  chain holds no shape, and otherwise multiplies the factor into the chain's
  candidate, which a later shape of the chain then carries into a `max`. That
  is the stolen step bound. The noise relief and the sine relief move to the
  cellular rule in every chain.
- **The point factor comes from the chain before the instruction, or the
  program is refused.** `SdfProgram.AnalyzeReliefPointFactors` computes it,
  first written for cellular relief: one over the smallest axis of each `Scale`, times the
  factor of each warp that has a global bound, and a refusal by name when the
  chain holds an operation with no global continuous derivative bound. The
  noise relief and the sine relief take that rule whole, masked or not. That
  supplies the `1 / s` whose absence is the wrong-units finding. The first
  draft kept today's charge for an unmasked relief after such an operation.
  The design review showed that charge is wrong twice over: it has no scale
  term, and nothing bounds the jump at a fold's seam. The lead then ruled that
  an unmasked `NoiseDisplace` after a fold is refused, as `CellDisplace`
  already is. The fallback is gone.
- **The relief's own numbers are the ones the bound was computed from.** `N`
  is at most one only if the packed normalization is the reciprocal of the
  octaves' gain sum, and `Γ` is right only for the packed octave count, gain
  and lacunarity. The builder computes the normalization, and the raw program
  door accepts whatever a caller packs. The design review supplied a
  normalization of 32 and got a relief 32 times its amplitude. So the relief
  is decoded once, at the program door, into a typed value that validates the
  octave count, gain and lacunarity against the builder's domains and derives
  the normalization itself. The analysis, the builder and both evaluators'
  compiles read that value. The door refuses an instruction whose packed
  normalization is not the derived one. Cellular relief already has such a
  value (`SdfCellDisplacement`).
  [Step 1](#1-the-program-door-derives-every-lane-the-builder-computes) makes
  the same rule general.

What the refusal turns away is narrow. A creation emits its noise relief after
the stamp's prefix and its sine relief after a reset, a translation and a
rotation; both pass. A `puck.sdf.v1` document can write a noise relief after a
cell fold without a reset between them (`SdfDocumentDecoder`), and that
document is now refused. It has no sine relief operation. A reach-dependent
warp before either relief is reachable only through the builder.

### Skipping a far mask exactly

Far from a region the weight is `outside` and nothing needs evaluating. The
skip must be exact: if skipping a shape could change the weight, the weight
would jump on the skip's boundary, and no slope bound covers a jump.

A shape may be skipped where its distance is certainly at least `end`. Some
admitted shapes read less than the true distance: an ellipsoid's field grows
only `min(r) / max(r)` as fast as distance along its long axis. So a skip
needs a certificate of the form "this shape reads at least `gain` times the
distance to this sphere", and exactness is a statement about one reader's
arithmetic. The first draft derived one certificate from the float program and
gave it to both readers. The design review showed a thin ellipsoid whose
smallest radius rounds from 7.49 to 7 steps of the fixed-point grid: the fixed
reader's field grows more slowly than the float gain says, so the shared skip
answered `outside` where the fixed reader reads a weight of 0.9956.

Each reader therefore skips only on its own certificate, and both
certificates exist:

- **The kernel** uses the program's distance gain
  (`SdfProgram.InstanceDistanceGain` and the per-shape rule behind it), which
  is derived from the packed float lanes the kernel evaluates and already
  lets the kernels reject a whole instance. The analysis runs over the
  instructions the shape's own lowering emitted, so a residual non-uniform
  scale lowers the gain as it does in the stream. A shape's skip sphere is its
  bounding sphere grown by `end / gain`, packed with the shape.
- **The fixed-point evaluator** uses its reach certificates
  (`SdfFieldEvaluator.Certificates.cs`), which its cull already builds from
  the compiled fixed-point instructions, never from the float program, rounded
  outward. A region shape gets one the way a stream shape does, from its pose
  and its compiled primitive.

A shape a reader has no certificate for is always evaluated by that reader.
The fixed-point analysis's header says a non-uniform scale and every fold
leave a chain uncertified, and the evaluator applies an elongation as a fold,
so a region shape with either is never skipped in contact.
A whole mask is skipped only when every one of its shapes is certified for
that reader, so a mask that holds a plane is never skipped whole. This design
adds no certificate rule of its own.

The two readers may skip at different distances. Neither skip changes the
weight its reader would have computed, so the fields do not move.

### What bake and contact read

A band narrower than the bake's coarse cell or the collider's gradient taps is
invisible to both today: each sizes its samples from the noise's wavelength
alone. The compiled mask gives the two facts they need, the band's width and
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
| The weight lies in [0, 1] | `SdfReliefBand`'s constructor: weights are 16-bit fractions, and no other code makes a band | Nowhere |
| The relief is the one its bound was computed from: its noise is at most one in magnitude, so a masked relief reaches no farther than its amplitude | The typed relief value decoded at the program door, which derives the normalization and validates the octave count, gain and lacunarity; the analysis and both compiles read that value | The kernel reads the packed lane, which the door has compared with the derived one |
| The weight is continuous, with slope at most the band's in the relief's frame | The types: a region shape is lowered by its own constructor from an author's specification, so no caller supplies a packed lane; it has no field that could hold a fold, a warp, a blend or a scope; its packed type comes from a closed list; and its relief's chain passes the point-factor rule or the program is refused | Each listed shape's own 1-Lipschitz property is that shape's, held by `SdfPrimitiveLipschitzLawTests`. The exact skip is arithmetic at an edge and stays review-only |
| The relief's step bound includes the band's slope in the right units | One formula (`SdfProgram.NoiseDisplaceStepFactor`), fed one point factor per instruction, added at the relief's own instruction. Noise and bands are in one frame, so no caller converts units | The point-factor rule's own list of bounded operations |
| A mask composes nothing into the field | Structure: a mask is not an instruction. There is no accumulator write, push or pop to get wrong, and the ReliefWeight op is deleted | Nowhere |
| CPU and GPU read the same field | One operand with two readers; a band whose start, width and end both carriers hold exactly; one shape order fixed by the type; one vocabulary list; `SdfEncodingProbe` passing every packed field | The two readers agree by convention, as every instruction's do. This stays review-only, with the kernel-agreement leg as its check |
| Bake and contact see the band | The compiled mask's width and box, which the two consumers read | Nothing forces a future consumer to read them. The bake's sample budget and the collider's gradient floor still bound how narrow a band either resolves |
| A mask is part of its relief's identity: every key, excerpt and rewrite that carries the relief carries its masks | An instruction and its operand are one immutable value, and no interface takes one without the other | A reader that hashes a program's packed words rather than its pairs still sees the operand, since it is in the words |

## What stays review-only

[The hazards page](../../development/hazards.md#silent-invariants) lists the
classes that break without a symptom. This design leaves three of them open
and names them so the implementing change gets an adversarial review of
exactly these:

1. **The packed operand's encoder and decoder.** The C# packer, the kernel
   function and the fixed-point reader agree on the operand's rows by
   convention, like the rest of the instruction set.
2. **The exact skip.** Each reader's skip rests on a certificate that already
   exists and is already in this class. The new use needs counterexample
   tests at the arithmetic edge: points one raw either side of each skip
   sphere, for every listed shape type, in each reader, including a shape
   whose smallest radius is a few raws.
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
- **A shape a few raws thin reads differently in the two readers.** The
  design review's ellipsoid has a radius of 7.49 raws in float and 7 in fixed
  point, and its two fields differ by that ratio far from it. That is true of
  every shape in the stream as well. The design makes each reader's skip
  exact for its own field; it does not make the two fields equal.
- **The shading octaves** the terrain's materials will read are not masked by
  this design. Because a mask is now the kind of value a paint is, the
  shading stage can evaluate the same mask at the hit as it evaluates a paint,
  with no per-vertex weight from the bake. The terrain plan's shading step
  owns that choice.

## The design review's findings, answered

A review of this memo's first draft found six flaws, all high, before its
session ended; what it would have found past the sixth is unknown. Each is
checked against the code and the arithmetic, and each holds.

| Finding | Cause in the first draft | What closes it | Step |
|---|---|---|---|
| The band's end can leave the shared grid: a start one step under the limit with a width of two steps has an end float rounds onto the limit, so the kernel reads `outside` where Q48.16 reads one half (F1) | The constructor bounded the start and the width and never the end, which the kernel compares against | The constructor requires the start, the width and the end all to be grid values within the limit, and the memo states the four stored numbers and the endpoint rule together ([the weight](#the-weight), [the band's carrier](#the-bands-carrier)) | 4 |
| The fallback for an unmasked relief keeps the scaled-relief undercharge and accepts a seam jump: after a scale of 1/64 and a repeat, a unit sine relief has a slope of 64 against a recorded bound of 2 (F2) | An unmasked relief after an operation the point-factor rule cannot bound kept today's charge, so that nothing building today was refused | The fallback is deleted. The noise and sine reliefs take the cellular rule whole: a certified prefix is charged with its scale, and any other is refused. The lead's ruling covers the one case an addon can write | 2 |
| The render distance gain is too large for some fixed-point masks: a radius of 7.49 raws compiles to 7, and the shared skip answers `outside` where the fixed reader reads 0.9956 (F3) | One certificate, derived from float lanes, was given to both readers | Each reader skips on its own existing certificate, derived from the parameters that reader evaluates. A shape without one is always evaluated, and a whole mask is skipped only when every shape is certified ([the skip](#skipping-a-far-mask-exactly)) | 4 |
| The capture accepts forged and mutable payloads: a hand-made plane capture with a normal of length 32 is admitted with a band 32 times steeper than charged, a polygon's vertex list can change after packing, and a type tag cannot tell an authored revolve from a cone's (F4) | The region door took a captured shape and checked its tag, and the capture is a public record over caller-owned data | The door takes the author's specification and lowers it itself. The capture has no public constructor and owns a frozen side table. The revolve refusal reads the specification, and the three internal revolve lowerings are named as admitted ([the compiled mask](#the-compiled-mask)) | 4 |
| The registry does not keep ownership through a rewrite: `Excerpt` gives a rewritten instruction's entry to the last instruction emitted, so a relief copied twice loses its masks on the first copy and a relief rewritten to nothing gives them to its neighbour; a copied instruction keeps a packed place from another layout (F5) | An operand was looked up by its instruction's index, after the rewrite | An instruction and its operand are one immutable value through the constructor and the rewrite; a typed instruction holds no packed place; the packed operand is self-relative with its counts stored; the key hashes the operand's logical value ([how a mask travels](#how-a-mask-travels-with-its-relief)) | 3 |
| The packed noise can exceed its amplitude: a hand-packed normalization of 32 passes the raw door, the relief reaches 32 times its amplitude and its slope exceeds the recorded bound (F6) | The first step re-derived a scale's correction and checked rotations, and nothing else | The relief is decoded at the program door into a typed value that derives the normalization and validates its domains, and the bound reads that value. The first step is widened to every lane the builder computes | 1 |

## The second review's findings, answered

| Finding | Holds on the tree? | What closes it | Step |
|---|---|---|---|
| A mask inherits a discontinuous fold from point operations before its first shape (R1) | Yes: admission restricts the instructions after the seeded push and never requires a reset before the first shape | A region shape's pose starts from the relief's point and is the whole of its frame; the type has no fold. The relief's own chain passes the point-factor rule or the program is refused | 2, 4 |
| A later shape takes an earlier relief's step-bound charge (R2) | Yes: the analysis asks whether the whole chain holds a shape | The relief is charged at its own instruction in every chain | 2 |
| Independently rounded endpoints collapse a valid float band on the CPU (R3) | Yes: the evaluator's compile rounds each lane on its own | The band's start, width and end are grid values both carriers hold exactly; a width below one step is refused | 4 |
| Admission accepts a sampled region, which jumps and which the CPU cannot read (R4) | Yes: admission excludes a plane and a detail shape and nothing else | The closed list of shape types, with no default that admits | 4 |
| A hand-packed scale lane makes the region steeper than its bound (R5) | Yes, and for surface shapes too: packed admission never compares a `Scale`'s correction with its axes | A region shape's correction comes from its own lowering. For the stream, the program door derives every lane the builder computes | 1, 4 |
| Skinning consumes an owner at each mask shape and refuses the skin (R6) | Yes: the influence walk takes an owner per shape instruction | No mask shape is an instruction, so the owner list and the walk count the same shapes | 4 |
| A canonical mask pop still changes the saved field (R7) | Yes, for a parent above the far distance | There is no pop | 4 |
| A scaled creation's noise derivative is in the wrong units (R8) | Yes, and without any mask: the charge multiplies a world amplitude by a creation frequency | The point factor of the relief's chain, with no uncertified case left | 2 |
| A plane mask passes document validation and fails at field build (R9) | Yes | One door answers both callers, and it admits a plane | 4 |
| Bake refinement and contact normals miss a narrow band (R10) | Yes; the missed contour is unconfirmed at runtime | The mask's width and box, read by the parts declaration and the collider | 5 |

## The first review's findings, answered

The round of fixes closed some of these and the second review reopened
others. The answer here is against the new construct, not the fixes.

| Finding | What became of it | What closes it |
|---|---|---|
| A mask chain absorbs its relief's step bound (M1) | Reopened as R2 | The per-instruction charge |
| Packed admission accepts a seed that cuts the band short, and a seed can overflow (M2) | Closed by deriving the seed from the band | There is no seed and no rescale by a region bound: the end is the band's own number, and the region's distance is 1-Lipschitz by construction |
| An admitted pop lowers the field and invalidates the instance cull (M3) | Reopened as R7 | There is no pop, and the cull has no mask scope to step over |
| Member extraction gives mask shapes a slot and can build an empty scope (M4) | Closed by a table of mask instructions | No mask instruction exists to drop or keep; a relief the excerpt keeps is kept with its masks, as one value |
| A pending weight crosses an instance boundary (M5) | Closed by an owner check | No weight is pending between instructions: a relief owns its masks |
| A repeat seam puts a jump in the weight (M6) | Reopened as R1 | The types and the point-factor rule |
| Detail shapes make the CPU and GPU masks disagree (M7) | Closed by a refusal | A region shape has no detail flag |
| The CPU rounds the mask's reciprocal to zero (M8) | Reopened as R3 | No reader stores a reciprocal; both divide by an exact width |
| Bake refinement and contact sampling ignore the mask's feature size (M9) | Still open, as R10 | Step 5 |
| Audit excerpts lose point transforms a mask left behind (M10) | Closed by tracking a mask's point operations | A mask moves no point |
| Surface classifiers count mask materials and unbounded geometry (M11) | Closed by a shared reading; the plane half reopened as R9 | Classifiers read shapes, and a region shape is not one |

## Options weighed

| Option | What it is | What it closes | Why not, or why |
|---|---|---|---|
| Patch the scope a third time | A check per finding in `RequireReliefMasks` and its partners | The ten findings as stated | The five causes stay. Each new reader, opcode or numeric edge is exposed as these were |
| Keep the mask in the stream under its own opcodes | A begin and an end that save and restore all walk state, and a region-shape opcode that shares the surface's arm | Surface classification, by the opcode. Composition, by restoring | Chains, segments, instance owners, slot tracking and excerpts are still computed over every instruction, so each of those analyses still owes the mask a rule |
| A region sub-program in a second instruction list | The region's instructions, interpreted by a restricted interpreter in each reader | Everything the chosen form closes | Three small interpreters that must each refuse the same opcodes and mirror the main arms. A value with a fixed order needs no dispatch and cannot hold an opcode |
| **An operand that travels with its relief, as typed values** | This memo | All 21 findings | It needs instructions and operands to travel as pairs, which changes the carrier of the four existing tables too, and it adds one more call site of the shape dispatch |
| A sampled weight lattice | Bake the weight to a grid and sample it | Continuity, by interpolation | A second representation of a region, a resolution that must beat the band, and a fixed-point sampler for contact. The plan already leaves painted masks for later |

The chosen form is not borrowed from another engine. It follows from two
things Puck already has: paints, which evaluate a captured shape for coverage
at a hit, and the data a program keeps beside its instructions. A mask is a
paint's sibling that the field reads instead of the shading stage.

## What the prototype settled

A numerical model of the masked relief, written in Python with numpy and run
in this lane without the engine, answered the questions below. It is scratch
and is not kept in the tree; its output is in the commits that add and revise
this memo. The model is a sphere with value-noise relief (quintic
interpolation of corner values in [−1, 1], normalized octaves), masked by
ellipsoid-gauge regions with smoothstep bands, under a uniform stamp scale.

The first run settled four questions.

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
   changed it by nothing in 400,000 points. *Decided:* a skip needs a gain.
4. **How far apart do the two carriers read one band?** For widths from 8
   down to 2⁻⁸ units, a float32 and a Q48.16 smoothstep at the same region
   distance differed by at most 3.4 · 10⁻⁵, about two steps of the weight's
   grid. *Decided:* no stored reciprocal and no tolerance beyond the shapes'
   own.

The second run took the design review's six counterexamples, each against the
first draft's rule and the revised one.

| Counterexample | First draft | Revised |
|---|---|---|
| A start of 4096 − 2⁻¹² with a width of 2⁻¹¹ (F1) | Admitted. The end, 4096 + 2⁻¹², is 4096 in float32; at a region distance of 4096 Q48.16 reads 0.5 and the kernel 1 | Refused, because the end is past the limit. Of 749,711 random bands inside the revised domain, none had an inexact float32 start, width or sum |
| A plane, then a scale of 1/64, a repeat of period 4 and a unit sine relief (F2) | Slope 64.0 in x at the review's point against a recorded bound of 2; a jump of 1.819 across the fold's seam | The fold is refused. With the scale alone the largest sampled slope is 64.97 against a certified bound of 65 |
| An ellipsoid of radii 1, 7.49 raws and 1 with a band one unit wide (F3) | The float certificate skips past 8750.8; at x = 9000 the fixed reader, whose radius is 7 raws, reads a distance of 0.9612 and a weight of 0.9956, and the skip answers 1 | The fixed reader's own certificate skips past 9363.3, so x = 9000 is evaluated; beyond it the least fixed distance in 400,000 points is 1.07, past the band's end |
| A hand-made plane capture with a normal of length 32 (F4) | Weight slope 48 against a charged 1.5 | Cannot be written: the door takes a plane's specification, which has no normal |
| A relief with masks rewritten twice, and rewritten to nothing (F5) | Twice: the first copy has no masks. To nothing: the sphere before it receives them | Twice: both copies have the masks. To nothing: no instruction has them |
| A one-octave relief of amplitude 1 with its normalization packed as 32 (F6) | The relief reaches 31.98 and its slope 118 against a recorded term of 6.50 | Refused: the door derives 1 and compares |

The fourth and fifth rows are arithmetic and a model of the rewrite's
bookkeeping, since their fix is a type and not a number. The model does not
settle the kernel's cost or either real reader's rounding at a skip sphere.
Steps 4 and 6 own those.

## Migration steps

Each step is one change that leaves the tree working, with the check that
shows it did. Steps 1 to 3 fix defects that exist without any mask.

### 1. The program door derives every lane the builder computes

Built. Every operation and shape type has one row in the lane contract
(`SdfProgram.LaneContractOf`, `SdfLaneContract`), written as switches with no
row that admits by default. A row classes each lane as authored (checked
against the builder's domain), derived (recomputed by the function the builder
calls, `SdfDerivedLanes`, and compared bit for bit), unit (within
`SdfProgram.UnitLengthTolerance`, which admits the fixed-point encoding
boundary's 2⁻¹⁶ normalization), zero, or written by the program itself (a side
table's place, a scope's baked scale, which `AnalyzeLipschitz` now writes
whatever the caller supplied, and a mask's band words). The noise relief is
decoded at the door into `SdfNoiseDisplacement`, which derives its
normalization and validates its domains; the analysis reads its slope from
that value, and the CPU evaluator's compile reads the lane the door compared.
`SdfLaneContractLawTests` holds the two reviews' streams as laws and nudges
every derived, zero and unit lane of every instruction `SdfEncodingProbe`
emits, each refused.

What the contract cannot derive: a glyph's sampling correction comes from its
atlas's pixels, which a program names only by hash, so the door checks its
range alone.

### 2. A relief is charged at its own instruction, through its chain's point factor

Built. `SdfProgram.AnalyzeReliefPointFactors` gives the point factor of every
relief (cellular, noise and sine), and `AnalyzeLipschitz` adds each one's slope
times that factor at its own instruction in every chain; the chain product for
`NoiseDisplace` and `Displace` and its shape-free test are gone. A relief after
an operation the analysis cannot bound is refused by name, naming that
operation. `SdfDocumentDecoder` refuses a `puck.sdf.v1` relief that follows a
`cellJitter` with no `reset` between them at decode (`SdfRefusal.ReliefAfterFold`),
naming both ops. The mask slopes the noise relief adds are still the built
table's, per field unit, until step 4. `SdfLaneContractLawTests` holds the
three gradient witnesses and the fold refusals, and
`ReliefStepBoundLawTests` holds the stamped scope's bound equal to
`CreationNoiseDocument.StepFactor` at scales 1, 64 and 1/64, masked and not.
A relief after a fold stays open work in
[the terrain plan](../terrain-pass.md#a-relief-after-a-fold).

### 3. An instruction and its operand travel as one value

The carrier in [how a mask travels](#how-a-mask-travels-with-its-relief), for
the four kinds of data a program holds today. The program's constructor,
`Excerpt`'s rewrite, the geometry key and the evaluator's compile move to
pairs. The index-keyed lists, their ownership checks and the key's
hand-written list of table-bearing shape types go.

*Check:* an excerpt that rewrites a convex polygon shape into two copies gives
both copies its vertices, and one that rewrites it into nothing leaves no
instruction holding them. No typed instruction of a built program holds a
nonzero place lane, and `SdfProgram.Of` refuses one that does. Two programs
that hold the same shapes at different packed places hash to the same geometry
key, and changing one vertex changes it. The debug world boots headless.

### 4. The construct

One change, because no half of it stands alone (rule 5: no second form
alongside):

- the three types and the door; `SdfPaintShape` closed, frozen, renamed and
  shared;
- the relief's operand as a fifth kind of pair, its packing, and the encoding
  probe's call;
- the kernel function and the evaluator's point and box rules, each with its
  own skip certificate;
- `NoiseDisplace` taking masks; `CreationStampEmitter.EmitNoise` compiling a
  document's masks once and passing them, with no scale applied;
- the canonicalizer validating through the door and storing snapped bands;
- everything in [what it deletes](#what-it-deletes);
- the plan's lowering and exactness sections, the
  [sync pair](../../../.claude/skills/rendering/references/sync-pairs.md#instruction-semantics-shared-by-both-interpreters)
  and the authoring skill rewritten to the new form in the same change.

*Checks:*

- `puck search` for the deleted names in `src/` and `tests/` finds nothing,
  and no public constructor of the captured shape remains.
- `SdfReliefMaskLawTests`, retargeted, holds the two properties it holds
  today (outside every band the masked field is the unmasked field bit for
  bit; on and inside a region of zero inside weight the relief is exactly
  zero) and gains one case per closed finding: the fold before a masked
  relief is refused; the collapsing band and the band whose end passes the
  limit are refused; the door refuses a sweep, an authored revolve and a path
  profile by name and admits a cone; it has no parameter that could carry a
  packed lane, a detail flag or a fold; two smooth-unioned slots under one
  masked relief still skin with two owners; a plane region builds, and its
  point and box rules agree.
- For every band on the domain's edges, the kernel's float end equals the
  fixed-point end, read from the packed words and the compiled operand.
- One case per listed shape type, and one for a shape whose smallest radius
  is a few raws, evaluates the weight in the fixed-point reader with skipping
  on and off at points one raw either side of its skip sphere and reads the
  same bits. The kernel's half of this check is in step 6.
- A mask edit changes the group's geometry key, and an excerpt that copies a
  masked relief twice reads the same weight under both copies.
- The debug world boots headless, and the north-west sector's recorded scope
  bound is what it was after step 2.
  `puck mesh audit --cpu --world worlds/debug --prototype valley-north-west`
  shows no new open edges or slivers.

### 5. Bake and contact read the band

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

### 6. Kernel agreement and kernel cost

A GPU leg for the lead's batched window. It needs a windowed World on both
backends and is the terrain plan's kernel-agreement step applied to the new
form.

- *Agreement:* `puck mesh audit` of the masked north-west sector against the
  kernel bake, on Direct3D 12 and Vulkan, within the bake's tolerance. The
  same audit on a prototype whose region shapes sit a little inside and a
  little outside their kernel skip spheres shows no step in the baked surface
  at a sphere.
- *Cost:* the bake and views kernels' bytecode size and compile time before
  and after step 4, and the benchmark route's frame statistics. *Decides:*
  whether the mask's reader keeps calling the whole shape dispatch. If a
  kernel grows past what its compile or its register budget tolerates, the
  reader calls a dispatch restricted to the closed list, generated from the
  same C# list so the two cannot differ.
- *Looks:* the plan's windowed captures of the lake and the gallery's RELIEF
  MASK exhibit, which must read as their plaques say.

### 7. One reader for a captured shape

`sdfApplyPaints` and `sdfShadeShape` each unpack a captured shape and apply
its scale, turn and correction in their own lines, and the mask's reader would
be a third copy. The three move to one kernel function over one packed block,
written by one C# packer.

*Check:* the views kernels' `[pipeline-statistics]` register counts are no
higher, and the gallery's paint exhibits read as their plaques say in the same
window as step 6.

## What it deletes

- the ReliefWeight op, its generated define, its rows in `SdfOpRoles` and the
  evaluator's supported set, and its arms in `mapCore`, the point walk, the
  box walk, the cull and the cell analysis.
- The seeded push: IsReliefMaskPush, the seed read in `mapCore`,
  and the seed branches of the evaluator's compile and both walks.
- The whole of `SdfProgram.ReliefMasks.cs`: `RequireReliefMasks` and its band
  check, `ReliefMaskInstructions`, both `IsSurfaceShape` overloads and their
  cache, the slope table and the endpoint writer. The slope formula moves into
  the band.
- From `AnalyzeLipschitz`: the `ReliefWeight` arm that rewrites the weight's
  and the push's words, the push table kept for it, the mask-slope plumbing
  through the chain pass, and the shape-free branch the noise and sine reliefs
  share.
- BeginReliefMask, EndReliefMask and
  the builder's open-mask state.
- ReliefMaskWeight as an instruction arm, the relief
  register in both walks and in `mapCore`, and the cull's step over a mask
  scope (`SdfFieldEvaluator.TryCertifyInstance`).
- The mask tables in `SdfMeshAudit.ShapeFields`, `SdfMeshAudit.SlotWalk` and
  the audit's own-part extraction, and the `IsSurfaceShape` calls in the bake
  group's drawn test and the glass classifier.
- The mask loop of `CreationStampEmitter.EmitNoise`, with its per-stamp
  scaling of the band, and the type refusals in
  `CreationCanonicalizer.ValidateReliefMask`, which the door now answers.
- From the program's carrier (step 3): the index-keyed lists the constructor
  takes, the checks that match each list to its instructions, the place lanes
  patched into typed instructions, `SdfProgram.SideTableValues` and the
  geometry key's list of table-bearing shape types.
- The public constructor of `SdfPaintShape`, and the checks `SdfPaint` makes
  on a shape's lanes because any caller could have written them.

## Systems considered

- **Field scopes** (`SdfProgramBuilder.PushField`): the built form. Rejected
  for the mask as above; unchanged for everything else.
- **Paints and the captured shape** (`SdfPaintShape`, `SdfPaint`,
  `sdfApplyPaints`): the capture is reused, behind the region shape's own
  constructor, and closed. A paint already is a shape evaluated for coverage
  over a smoothstep band, outside the field.
- **Authored shading's shape operation** (`sdfShadeShape`): the second reader
  of the same capture, and the reason step 7 exists.
- **The rigid-leaf plan** (`SdfProgram.CompileRigidPlan`): a compiled pose
  plus a tight bound sphere, walked with no opcode dispatch. It is the model
  for the mask's packed rows and skip test. It cannot be the mask's form,
  because a leaf names a shape instruction in the stream and carries no scale
  or elongation.
- **Program side tables** (`SdfProgram.SideTableValues`, `SdfCompiledPath`)
  and their admission (`SdfProgram.ValidatePackedContract` and the checks that
  match a profile to its polygon): the pattern the operand follows, replaced
  by pairs for every kind, because a lookup by index after a rewrite cannot
  keep ownership.
- **The cellular relief's point factors** (now `SdfProgram.AnalyzeReliefPointFactors`)
  and its typed value (`SdfCellDisplacement`): reused for the noise and sine
  reliefs. Between them they already hold the scale term, the refusal and the
  decode-once pattern the reviews asked for.
- **Distance gain** (`SdfProgram.InstanceDistanceGain`): reused as the
  kernel's skip certificate.
- **The fixed-point reach certificates** (`SdfFieldEvaluator.Certificates.cs`):
  reused as the fixed reader's skip certificate. They are derived from
  compiled instructions and rounded outward, which is what the design
  review's thin ellipsoid needs.
- **The encoding probe** (`SdfEncodingProbe`) and the packed-contract law
  (`SdfPackedContractLawTests`): reused to show the lane contract of step 1
  covers every lane an emitter packs.
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

Steps 1 and 2 closed the sine relief's stolen charge, the raw door's trust in
derived lanes and the scale's smallest axis spelled in several places
(`SdfDerivedLanes.ScaleCorrection` is now the one place).

## Open questions and their owners

- **Does the kernel tolerate a fourth site of the shape dispatch?** *Owner:*
  step 6, with the fallback named there.
- **Does the lattice's budget reach a band of a few millimetres?** *Owner:*
  step 5, whose check reports the shortfall if there is one.
- **What would let a relief follow a fold again?** A marcher and a bake that
  handle a seam's jump explicitly, with the relief charged inside each cell
  through the prefix's scale. Nothing designs that. *Owner:* step 2, which
  lands the refusal and records this in the terrain plan as open work, with
  the `puck.sdf.v1` document it turns away.
- **What did the second design review find?** Seven more flaws in this
  revision, five of them high. *Owner:* [the step back](#the-step-back), which
  answers each and changes the form.

## The step back

Every review round so far has found high defects: two rounds on the built
scope, then the rounds on this design, the last of which found seven more
flaws (D1 to D7) in the revision above. This section steps back from the
construct to the need. It asks what the lake and the gallery want from a mask,
whether that takes a general construct, and what smaller form would hold its
invariants by structure. It recommends one; the lead chose it, and step A is
built ([as built](#step-a-as-built-and-the-step-back-reviews-findings)). Nothing
here was measured on a GPU.

### The recommendation in brief

- **The need is real and small.** Terrain relief must be absent over some
  regions and return smoothly around them. The debug world has two authored
  uses and three planned ones. Each draws its region with one to a dozen
  boxes, ellipsoids or capsules, and each authored band is wider than the
  finest wavelength of the relief it weights.
- **It needs a weight, and it does not need a general construct.** Composing
  shapes after the relief leaves a cut or a fillet where relieved ground meets
  smooth ground, never a fade, so a weight on the relief's amplitude is
  required. The design above then pays for generality no place uses: fourteen
  shape types under a residual scale, 65,025 shapes behind a far skip, bands
  of any width with bake parts of their own, and a new carrier for every table
  a program holds. Those four mechanisms drew eight of the thirteen findings
  the two design reviews raised.
- **The form.** A mask becomes a short run of data rows in the instruction
  stream, directly before the relief that reads them. A row is a new
  instruction that no reader acts on. Only the relief's own arm reads it. A
  region shape is one of four primitives, packed exactly as a shape
  instruction packs it. There is no far skip, no side table, no change of
  carrier and no bake part.
- **What it keeps from the design above.** The band and its grid, the
  weight's formula, evaluation in the relief's own frame, the step bound, and
  migration steps 1 and 2, which fix defects that exist without any mask.
- **What it drops.** Migration steps 3 and 7, step 5's bake part and flag,
  both skip certificates, and the closing and renaming of the paint's
  captured shape.
- **What an author loses against the landed mask.** A region shape may be a
  box, a sphere, an ellipsoid, a superellipsoid, a capsule or a plane, and no
  other type. It takes no elongation. A relief takes at most 8 masks and 32
  region shapes. Both authored masks fit as they are written.
- **What it costs.** One kernel edit, so one rebake of every stored mesh, as
  any form costs. Every sample of a masked creation evaluates each region
  shape of its relief, at most 32 of them. The kernel's cost is not measured
  here; [step C](#c-kernel-agreement-kernel-cost-and-the-look) measures it.

### What the places need

| Place | Region | Band | Is the region a shape of the creation? |
|---|---|---|---|
| The lake's bowl (`valley-north-west`) | Two ellipsoids, the basin and its outlet, built from the rows that carve them (`terrainCarveMask` in `worlds/standard/terrain.puck`) | 8 m, no relief inside | Yes: the carve |
| The gallery's RELIEF MASK exhibit (`shape-noise-mask` in `worlds/debug/shapes.puck`), its bowl | One sphere | 8 cm, no relief inside | Yes: the carve |
| The same exhibit, its slab | One box a little larger than the slab | 6 cm, relief inside only | No |
| The glacier's cradle (planned) | One rounded box | Not authored yet | Yes: the carve |
| The falls' corridor (planned) | A chain of capsules along the falls' profile | A few metres | No |
| The overlook's pad (planned) | One box | Not authored yet | No |

Three facts about the need follow from the table.

1. A region is a few simple solids at a pose in the creation's frame.
2. A region is often the shape that carves, and not always. The falls carve
   nothing, and the slab's region is larger than the slab. So the weight
   cannot be read off the composition's own shapes.
3. Each authored band is wider than the finest wavelength the bake resolves
   in its relief: 8 m against 6.25 m at the lake, and 6 cm against 2.1 cm in
   the gallery. Those wavelengths are four times the spacing
   `CreationNoiseDocument.DetailSpacing` gives, worked by hand from its rule.

### Why the general construct keeps drawing findings

The second design review judged the design's core sound: a mask that is not
spelled in the surface's instructions, evaluated in the relief's frame, on a
band grid both carriers hold exactly, under the corrected step bound. Its
findings, and the first design review's, fall on mechanisms the general form
needs around that core.

| Mechanism | Why the general form needs it | Findings it drew |
|---|---|---|
| A far skip, with a certificate for each reader | A relief may hold 255 masks of 255 shapes | F3, D1, the frame-search half of D5 |
| A captured shape of the whole vocabulary, with a residual scale, a turn and an elongation | Any admitted primitive may be a region | F4, the compile and validation halves of D5, D7 |
| A new carrier for every table a program holds | The operand has a variable size and nested tables | F5, D3 |
| Bake parts and a refine flag for a band | A band may be far narrower than its relief's detail | D4, and R10 before it |
| The program door and the step bound, which are not the mask | They are wrong for unmasked reliefs today | F2, F6, D2, D6 |
| The band's numbers | Two carriers round an author's floats | F1, closed by the grid, which stays |

Each round closed its findings by adding rules to one of the first four
mechanisms, and the next round found the new rules' edges. That is the same
shape of failure the memo's opening describes for the built scope. The form
below removes those four mechanisms instead of adding to them.

### The options weighed again

| Option | Does it meet the need? | Its invariants, and what would enforce them | Cost | How much of the landed mask it deletes |
|---|---|---|---|---|
| The general operand: the design above, with D1 to D7 answered | Yes, and much more than the need | Eight. Five are structural once the carrier changes and each reader has a skip certificate | Migration steps 3 to 7; a fourth site of the kernel's shape dispatch; findings still arriving | All of it |
| No mask. Relief on a group of shapes, with carves and pads composed after it | No. A bowl carved after the relief is exact, but its rim follows the relief by up to the amplitude, against water placed from the analytic bowl. Relieved ground pokes through a pad unless the author also cuts it away. Nothing smooths a face that is not carved | None new: only scopes and blends that exist | A relief scope for a group in the creation document, and a transition built by hand at every site | All of it |
| A weight carried through the blends: each shape has a relief share, and a smooth blend mixes shares as it mixes skin owners (`SdfFieldEvaluator.ReadInfluences`) | Only where blends are smooth and as wide as the fade. A hard blend makes the share jump, and the field with it. The smooth radius is capped at half a metre today | Continuity would rest on every blend arm in both interpreters | Every blend path in the kernel and the fixed-point evaluator; the fade tied to the blend's rounding of the geometry | All of it |
| A weight lattice: a sampled field the relief multiplies by (`SdfProgramBuilder.SampledValues`) | Yes, painted masks included | Continuity by interpolation; a step bound computed from the stored values | A fixed-point sampler for contact, which does not exist (`SdfFieldEvaluator.IsSupportedShape` admits no sampled shape); a way to author or derive the values; a resolution finer than the band; memory for each sector | All of it |
| **The relief's own rows, with four primitives** | Yes, all five uses | Nine, in [the table below](#the-invariants-of-the-row-form); six hold by structure | One op in place of one; a do-nothing arm in each stream walker; a cap on region shapes | All but the document, the step formula and the law's two properties |

Two notes on the table. The second option's seam cannot be blended away: a
smooth union lowers the field by a quarter of its radius wherever its two
operands read the same value, and two solids that abut on a region's wall read
the same value all along it, so the blend would draw a ridge where the fade
should be. That is read from the blend's formula (`sdfBlendSmoothMinimum`, a
shared formula in `SdfFieldFormulas`) and was not run. And the lattice is not rejected for
good. When a sector's regions outgrow a short list, or an artist wants to
paint them, a lattice is a second source of the same weight: the relief's arm
multiplies by a number in [0, 1] with a declared slope either way. The plan
already leaves painted masks for later.

### The row form

**What an author writes.** The document is unchanged: `noise.masks` holds
entries of `shapes`, `fade`, `start`, `inside` and `outside`. Against the
landed mask, a plane may be a region shape and a band may end inside its
region. The band snaps to the grid of [the band's carrier](#the-bands-carrier),
as in the design above. The narrowings are the ones the summary lists.

**How it lowers.** `SdfProgramBuilder.NoiseDisplace` takes the relief's masks
and emits them as rows before its own instruction.

```text
PushField                         the creation's scope
  shapes …
  ResetPoint, the stamp's prefix  the relief's point chain, as today
  ReliefRow  band                 one mask: start, width, inside, outside
  ReliefRow  pose                 one region shape: its origin and turn in the relief's frame
  ReliefRow  primitive            that shape's type and two data vectors, as a shape instruction packs them
  … the mask's other shapes, then the relief's other masks …
  NoiseDisplace  rows: R          reads the R rows before it, then adds its displacement
PopField
```

`ReliefRow` is one new op, in place of the ReliefWeight op. In every walker
of the stream its arm does nothing: it moves no point, writes no field and
takes no owner. The relief's arm reads the rows, computes the weight and
multiplies its displacement by it. No value passes from one instruction to
another, so there is no register. A relief with no rows is today's
instruction to the bit, with zero in its free Material lane. The rows are the
same for every stamp of a document: the stamp's scale, turn, mirror and slot
are in the relief's point chain, and a region is posed in the frame that chain
reaches.

**The region shapes.** A new region shape value takes what an author writes:
a primitive, its scale, its rounding or exponent, and a pose. Its constructor
lowers the primitive through the native-dimension arms of
`SdfSolidGeometry.AppendScaledPrimitive`, the ones a non-uniform scale already
takes, and never through a `Scale` transform. A box packs its half-extents, a
sphere or an ellipsoid its radii, a capsule its endpoint and radius. The
constructor requires the result to be exactly one shape instruction with no
side table, and keeps that instruction's type and data vectors. The list is
written as a `switch` over the packed type with no default that admits: Box,
Superellipsoid, Capsule and Plane. A capsule must be scaled alike in X and Z,
the one case its native arm packs. A type joins the list when it meets three
conditions: it packs natively at any admitted scale, it is 1-Lipschitz, and
the fixed-point evaluator reads it. Adding one is one case in that list and
one in the kernel's switch.

**The weight.** As in [the weight](#the-weight) above, with the pose alone:
`u = turn_back(q − origin)`, `d = shape(u)`, a mask's distance the least of
its shapes', and the same three-branch band. Two early exits need no
certificate, because they follow from the weight's own definition. Once a
mask's running distance is at or below its band's start, its weight is
`inside` whatever the remaining shapes read. Once the product of weights is
exactly zero, the remaining masks and the noise are not evaluated.

The caps of 8 masks and 32 region shapes are proposed figures. They give the
kernel's loop a constant bound and keep a sample's mask work near the cost of
the relief's own octaves. They are not measured limits, and a creation that
needs more regions is the lattice's case.

**The readers.** Three must read the rows.

- The kernel's relief arm in `mapCore` loops over its rows and evaluates each
  region through a switch over the four listed types that calls the same
  primitive functions `evaluateShape` calls. The band's cubic is marked
  `precise` and clamps its parameter, which the second design review asked for
  under the compiler's fast arithmetic.
- `SdfFieldEvaluator`'s point walk evaluates a region through the evaluator's
  own shape evaluation, on a row compiled as a shape instruction's lanes are
  compiled. Its box walk bounds a region with the evaluator's existing shape
  bounds and reads the band at both ends of the interval, since the weight is
  monotone in the distance. The relief's interval rule already takes a weight
  interval.
- The step bound reads the bands of the relief's own run, at the relief's own
  charge (step 2), through the one formula
  (`SdfProgram.NoiseDisplaceStepFactor`).

Every other reader may ignore the rows and stays sound, because a weight only
shrinks the relief: the unmasked reach, bounds, culls and instance bound all
still hold.

**The door.** The program's constructor decodes each run once into typed
masks, a band through the band's own constructor, and both evaluators' compiles
and the step bound read those. It refuses, by name: a row that is not in a run
closed by a relief; a relief whose count is not its run's length; a run that
is not bands each followed by one or more pose and primitive pairs; a run and
its relief in different instances; more than 8 masks or 32 shapes; a primitive
type off the list; a turn that is not of unit length; and primitive lanes that
fail the lane contract step 1 gives that shape type. The builder has no method
that emits a row alone. `SdfProgram.Excerpt` carries a relief's run with the
relief and refuses a row named by itself.

**Bake and contact.** A band is read as one more wavelength of its relief.
`CreationNoiseDocument.DetailSpacing` answers the smaller of today's spacing
and a quarter of the narrowest band, and `WorldColliderSet.ReliefField` takes
the smaller of the finest octave's wavelength and the narrowest band before it
divides by sixteen. Both values are in creation units and are scaled by the
stamp where the wavelength already is. A mask whose two weights are equal is
the identity, and the canonical document omits it. Neither authored mask
changes its creation's spacing: a quarter of each band is above its relief's
spacing.

### The invariants of the row form

| Invariant | The one thing that enforces it | Where that stops |
|---|---|---|
| The weight lies in [0, 1], so a masked relief reaches no farther than its amplitude | The band's constructor, which the program door also decodes every band row through | Nowhere |
| A reader that ignores a mask stays sound | The first invariant: a weight only shrinks the relief | The step bound, which must add the bands' slopes |
| A region is never a surface and composes nothing | Structure: a row is its own op, and no arm for it writes the field, moves the point or takes an owner | Each stream walker needs the do-nothing arm. Where this lane read a walker with no arm for an op, it refuses or turns conservative: the point walk throws, the kernel returns its error hit, `SdfProgram.AnalyzeReliefPointFactors` refuses the cellular relief that follows (the rule step 2 gives every relief), and `SdfProgram.AnalyzeBounds` stops bounding the chain. The twin check of step A finds a missing arm |
| No weight passes between instructions | Structure: the relief's arm computes the weight from its own rows and uses it | Nowhere |
| The weight is continuous, its slope is at most the band's in the relief's frame, and the step bound includes that slope in the right units | A region is a listed primitive at a rigid pose, made only by its constructor and decoded only at the door; the relief's chain passes step 2's point-factor rule or the program is refused; one formula reads the bands | Each listed primitive's 1-Lipschitz property is its own, held by `SdfPrimitiveLipschitzLawTests` |
| A mask is part of its relief's identity | Rows are instructions, so the geometry key, a retained emission, capacity and upload carry them with no rule of their own; the door requires the run to be adjacent and counted; `Excerpt` carries it | A rewrite that separates a run from its relief is refused when the program is built. No type makes it impossible |
| The canonicalizer and the emitter agree on what a mask may hold | One typed door, which both call | Nowhere |
| CPU and GPU read the same field | The same rows; band values both carriers hold exactly; the same primitive functions the surface's shapes use | Convention, as for every instruction. Review-only, with step C's agreement leg as its check |
| Bake and contact resolve the band | One function on the relief's record, read where the wavelength is read | The lattice's sample budget, which already holds the north-west sector coarser than its relief declares. Nothing forces a future consumer to read it |

Three things stay review-only and are named for the implementing change's
review: the row packing against the kernel's reader and the door's decode; the
list of types against the kernel's switch; and the do-nothing arms, which the
twin check covers for the analyses and the interpreters' loud defaults cover
for themselves.

The row is new to the instruction set: no instruction today is data for
another. The built form failed because it spelled a region in the ops every
reader acts on, a shape and a scope. A row is an op no reader acts on. A
reader that has never heard of it refuses the program or loses tightness, and
never reads a region as geometry.

### The second design review's findings, answered

| Finding | Does it hold against the design above? | Under the row form |
|---|---|---|
| The kernel's skip sphere skips points inside a region when the band ends at or below zero, and assumes bounding spheres the program does not have for a superellipsoid, a non-uniform scale or an elongation (D1) | Yes | There is no skip. A band may end anywhere on the grid |
| Step 1's four lane classes do not fit a glyph, a regular polygon, a star, a pop or a mirror (D2) | Yes. It is not a mask finding | Unchanged: it is a finding against step 1. The row form asks step 1 for the contract of four shape types, none of which the finding names |
| The pair carrier breaks a sampled region's key and binding, drops operands across retained emissions, and leaves mask words out of the layout, the capacity measure and inspection (D3) | Yes | There is no carrier change. Rows are instructions, which each of those paths already carries, and a region has no side table |
| The band's bake part and contact step are wrong under a scaled stamp, for a plane, for a gauge that reads less than distance, for the group's coarse lattice, in the part's key, and for a mask of equal weights (D4) | Yes, all six | There is no part and no flag. The band's width is in creation units and is scaled where the wavelength is. A plane needs no box. A gauge that reads less than distance makes the band wider, never finer. A mask of equal weights is omitted |
| The contact compile refuses a residual non-uniform scale; the frame search needs bounds no skip has removed; the region's constructor validates no primitive; and the memo misquotes the built validator (D5) | Yes | No region has a residual scale, and there is no skip. The constructor lowers through the native arms, which refuse a primitive they cannot emit. The list has no prism, so no profile rule is needed. The sentence above that says the built validator refuses a path profile is wrong: it refuses a regular polygon profile |
| After step 2 an unscoped relief no longer binds the program's step scale, and the document decoder lacks the cellular relief's fold refusal (D6) | Yes. It is not a mask finding | Unchanged: it belongs to step 2 |
| One packed block for paints, shading and masks conflicts with the sweeps, revolves and box culls paints carry; the kernel's band needs `precise` and a clamp (D7) | Yes | Step 7 leaves the mask's path, and paints are not touched. The band's rule is `precise` and clamped |

### The earlier findings under the row form

These are the findings of the two reviews of the built scope and of the first
design review, each listed once.

| Findings | What closes them |
|---|---|
| A mask inherits a fold, or jumps at a repeat's seam (R1, M6) | A region has a pose and nothing else, and the relief's chain passes step 2's rule |
| A relief's step-bound charge is absorbed or taken by another shape (R2, M1) | Step 2, with the slopes read from the relief's own run |
| A band collapses, its reciprocal rounds to zero, or its end leaves the grid (R3, M8, F1) | The band's grid, unchanged |
| A sampled region is admitted (R4) | The list of four |
| A hand-packed scale lane steepens a region (R5) | A row has no scale lane, and its primitive lanes pass step 1's contract |
| Skinning takes an owner at each mask shape (R6) | A row is not a shape instruction, so the influence walk takes no owner for it |
| A mask's pop changes or lowers the saved field (R7, M3) | There is no scope and no pop |
| A scaled creation's relief is charged in the wrong units, and the fallback keeps the error (R8, F2) | Step 2 |
| A plane passes one validator and fails the other; classifiers count mask shapes (R9, M11) | One typed door; a plane is on the list; classifiers read shape instructions |
| Bake and contact miss a narrow band (R10, M9) | The band as a wavelength |
| A seed cuts the band short (M2) | There is no seed |
| Member extraction gives mask shapes a slot or builds an empty scope (M4) | `Excerpt` carries a relief's run, and a row named alone is refused |
| A pending weight crosses an instance boundary (M5) | No weight is pending, and the door keeps a run and its relief in one instance |
| A detail shape makes the two masks disagree (M7) | A row has no detail flag: `SdfProgram.ValidatePackedContract` already refuses the flag on anything but a shape instruction |
| An audit excerpt loses point transforms a mask left behind (M10) | A row moves no point |
| One skip certificate is wrong for the other reader (F3) | There is no skip |
| A capture accepts forged and mutable payloads (F4) | There is no capture. A region is lowered by its own constructor, a hand-packed plane's normal fails step 1's unit-length check, and no side table exists to change |
| A rewrite loses or misplaces an operand (F5) | Nothing is looked up by index. A relief copied without its rows is refused, and a row holds no packed place |
| A packed normalization exceeds the amplitude (F6) | Step 1 |

### Migration and checks

Steps 1 and 2 stand as written above, and D2 and D6 are theirs to answer.
Three steps replace steps 3 to 7.

#### A. The row form

One change, since no half of it stands alone:

- the band, the region shape and the mask as typed values, with the door that
  decodes a run into them;
- the row op in place of the ReliefWeight op, its packing, its lane contract
  and the encoding probe's call for every row kind and listed type;
- the relief's arm in `mapCore` and in the evaluator's point and box walks,
  and a do-nothing arm for the row in every other walker;
- `SdfProgramBuilder.NoiseDisplace` taking masks, and
  `CreationStampEmitter.EmitNoise` compiling a document's masks once and
  passing them with no scale applied;
- the canonicalizer validating through the door, storing snapped bands and
  omitting a mask of equal weights;
- everything in [what the row form deletes](#what-the-row-form-deletes);
- the terrain plan's lowering and exactness sections, the
  [sync pair](../../../.claude/skills/rendering/references/sync-pairs.md#instruction-semantics-shared-by-both-interpreters)
  and the authoring skill rewritten to the row form.

*Checks:*

- `puck search` for `ReliefWeight`, `BeginReliefMask`, `IsReliefMaskPush`,
  `ReliefMaskInstructions` and `IsSurfaceShape` in `src/` and `tests/` finds
  nothing.
- `SdfReliefMaskLawTests`, retargeted, holds its two properties: outside every
  band the masked field is the unmasked field bit for bit, and on and inside a
  region of zero inside weight the relief is exactly zero.
- The twin check: for each masked program the laws build, removing the rows
  and zeroing the relief's count leaves the segments, the skip spheres, the
  distance gain, the rigid plan, the point factors and the instance bounds
  equal once the rows' own indices are taken out, and changes the recorded
  step bound by exactly the amplitude times the summed slopes times the point
  factor.
- The door refuses each of these by name: a row with no relief after its run;
  a count that is not the run's length; a run split by an instance boundary; a
  thirty-third shape; a type off the list; a band off the grid or past its
  limit; a turn of length two; a plane row whose normal has length 32.
- The reviews' witnesses, replayed: the fold before a masked relief is refused
  (R1); two smooth-unioned slots under one masked relief skin with two owners
  (R6); the dilated sphere reads the same sample with its mask and without
  (R7); a plane region builds, and its point and box rules agree (R9).
- An excerpt that names a masked relief reads the same weight as its source;
  one that names a row alone is refused; a rewrite that drops a row is refused
  when the excerpt is built.
- Changing any one row lane changes the group's geometry key.
- Each listed type lowers to exactly one shape instruction at a uniform scale
  and at a non-uniform one.
- The debug world boots headless.
  `puck mesh audit --cpu --world worlds/debug --prototype valley-north-west`
  shows no new open edges or slivers, and `puck creation stats` prints the
  north-west sector's clamp as it does today.

#### Step A as built, and the step-back review's findings

Step A is built as the row form describes, with these choices made while
building it.

- **The row's lanes.** The Blend lane names the kind (`SdfReliefRowKind`).
  Every lane of every kind is classed by the lane contract
  (`SdfProgram.RequireReliefRowLanes`); a relief's Material lane is the count,
  checked against the run by the door (`SdfProgram.DecodeReliefRuns`).
- **A sphere region** lowers through the native arm to an exponent-2
  superellipsoid, so the list has four packed types and a sphere is one of
  them by lowering.
- **The superellipsoid and the capsule** each read one formula in both
  readers, a fix that stands without any mask: a superellipsoid's radii and
  exponent snap to the grid both carriers hold (`SdfDerivedLanes.FixedGrid`),
  its smallest radius rises to 2⁻¹³, the kernel carries its gauge ratios in
  metres as the fixed-point evaluator does, and both clamp the norm for
  2 < e ≤ 3; a capsule projects by its endpoint's own squared length in both,
  and its derived reciprocal lane is gone.
- **The unscoped relief** (D6) binds the step scale and is named by
  `SdfStepScaleBinder`, which now carries an op and an optional shape.

The step-back review's findings, each answered:

| Finding | What answers it |
|---|---|
| F1.1, a capsule's squared length overflows to a zero reciprocal | The lane contract refuses an endpoint whose squared length is not finite in float, and no reciprocal lane remains; a row also caps every magnitude (F1.4) |
| F1.2, row lanes the contract leaves unconstrained, and the relief's Material lane | Each row kind classes every lane: a band's Shape and Data1 zero, a pose's Shape and Data0.w zero and its quaternion unit, a primitive's Data1.x and Material zero and its lanes through the shape's own row. `SdfNoiseDisplacement.Of` bounds the count by the cap and the door checks it against the run |
| F1.3, the backward read in `mapCore` | The door requires a contiguous run in the relief's owner, so the run lies in its segment; the kernel returns its error hit for a count past `SDF_MAX_RELIEF_ROWS` or the segment's start, and its row loop has that constant bound |
| F1.4, Q48.16 saturation on large finite floats | Every row lane is at most `SdfReliefRegion.MaxMagnitude` (2²⁰). The same saturation on an ordinary shape's lanes is left open (see below) |
| F1.5, bounds and rotation bounds | The box rule answers the unbounded interval when a region's interval is unbounded, a region's turn has its interval constants built once at compile, and a weight of exactly zero evaluates no noise in either walk |
| F1.6, the unit tolerance on poses | A mask's slope carries the regions' stretch, `1 + 4 · 2⁻¹³` (`SdfReliefRegion.MaxStretch`), which covers a turn and a plane normal at the tolerance's edge |
| F2.1, the uniform-scale path and chamfered boxes | `SdfSolidGeometry.AppendNativePrimitive` holds the native arms, which `AppendScaledPrimitive` and the region's constructor both call; the constructor requires one listed instruction, so a chamfered box and a capsule scaled unlike in X and Z are refused |
| F2.2, the superellipsoid's small-radius divergence and its clamp | The grid snap and one formula above |
| F2.3, the capsule reads different lanes | One formula from the endpoint above |
| F3.1, `Excerpt` against its callers | `Excerpt` emits a relief's run before it unless the excerpt's last instructions are already those rows in order; a row named alone, or a rewrite that drops or repeats one, leaves a run the excerpt's program refuses |
| F3.2, the instance cull | `TryCertifyInstance` steps over a row; the seeded-push skip is gone |
| F3.3, the row's role | `SdfOpRoles` classes a row as a field op, so the audit's member and shape extraction keep or drop it with its relief |
| F3.4, the mask slope's units | A masked relief is charged its masked slope times its point factor (`SdfNoiseDisplacement.MaskedSlope`) |
| F3.5, conservative defaults | `AnalyzeSegment`, the distance gain, the shape reaches and the point factors each have a do-nothing arm for a row |
| F4.1, D6 | The binder above |

The twin check is `SdfReliefMaskLawTests.RemovingTheRowsChangesOnlyTheStepBoundByTheMasksCharge`,
at scale one and at a quarter. It compares the instructions, the skip segments,
the global step scale and its binder and the dynamic capacity, and holds the
scope's bound to the twin's plus the masks' charge. The evaluator's frame is
not part of it: a region's interval walk can narrow the frame (at a quarter
scale the masked lake's frame is a quarter of the twin's, 2⁴⁴ raws against
2⁴⁶), which shrinks no query anyone makes. The instance cull's certificate is
not observable from outside the evaluator, so its do-nothing arm is reviewed,
not checked.

Still open after step A: an ordinary shape's lanes saturate Q48.16 past
about 1.4·10¹⁴ as F1.4 describes for rows; the kernel's agreement, its cost and
the looks are step C's.

#### B. The band as a wavelength

`CreationNoiseDocument.DetailSpacing` and `WorldColliderSet.ReliefField` read
the narrowest band, through one member of the relief's record.

*Check:* the spacing of the north-west sector and of the gallery exhibit is
what it was. The reviews' case, built as a test prototype (a broad flat box
with one slow octave, relief suppressed in a 2 cm strip with 5 mm fades),
declares a spacing of a quarter of the fade. `puck mesh audit --cpu` reads
the surface inside the strip at the unrelieved height within one fine cell,
and the collider's normal at a fade's midpoint is within a few degrees of the
analytic one. If the lattice's sample budget cannot reach the strip, the audit
shows by how much and the step's commit says so.

#### C. Kernel agreement, kernel cost and the look

A GPU leg for the lead's batched window, on both backends.

- *Agreement:* `puck mesh audit` of the masked north-west sector against the
  kernel bake, within the bake's tolerance, and the same for a prototype that
  holds one region of every listed type.
- *Cost:* the bake kernels' bytecode size, and the north-west sector's bake
  as its done line reports it (the `gpu-work` counts and each job's GPU time),
  before and after step A. *Decides:* whether the rows stay before the relief.
  Each row costs the kernel's main loop one dispatch that does nothing. If
  that shows in the readings, the run moves behind the relief and the relief's
  arm steps over it.
- *Looks:* the plan's captures of the lake and of the gallery's RELIEF MASK
  exhibit, which must read as their plaques say.

### What the row form deletes

Everything [what it deletes](#what-it-deletes) lists for the mask itself: the
`ReliefWeight` op and its arms, the seeded push, the whole of
`SdfProgram.ReliefMasks.cs`, the builder's begin and end and its open-mask
state, the relief register in both walks and in `mapCore`, the cull's step
over a mask scope, the mask tables in the audit and the two classifiers'
calls, and the emitter's mask loop with its per-stamp scaling.

It does not touch what that list deletes from the program's carrier or from
paints: the index-keyed lists, `SdfProgram.SideTableValues`, the geometry key's
list of table-bearing types and `SdfPaintShape`'s public constructor all stay.
The carrier change (step 3) and the shared paint reader (step 7) may still be
worth doing for their own sake. Neither is on the mask's path any more, and
the second design review's D3 and D7 say what each would have to carry.

It keeps the document (`CreationReliefMaskDocument`), the step formula with
its mask slope, the law's two properties and both authored masks.

### Systems considered for the step back

- **The instruction stream as the carrier.** `SdfMeshGeometryBuilder.AppendInstance`
  hashes every instruction in an instance's range, and
  `SdfProgramBuilder.RetainEmission` copies ranges of instructions. A row
  rides both, and the capacity measure, with no rule of its own. That is the
  reason for rows over a fifth side table.
- **Program side tables** (`SdfProgram.SideTableValues`). Four index-keyed
  lists run in parallel through the builder, a retained emission, the
  constructor, the excerpt, inspection and capacity. A fifth would repeat all
  of that, and a table missing from the key's list bakes nothing on an edit.
  Not extended.
- **The native-dimension lowering** (`SdfSolidGeometry.AppendScaledPrimitive`).
  Reused for a region shape, with its arms callable without the uniform-scale
  path.
- **The evaluator's shape evaluation and shape bounds** (`SdfFieldEvaluator`),
  and the primitive functions behind `evaluateShape`. Reused, so a region is
  evaluated as an ordinary shape and no second vocabulary exists.
- **The decode-once typed value** (`SdfCellDisplacement`, and the relief's own
  in step 1). The pattern the door's typed masks follow.
- **Paints and the captured shape** (`SdfPaintShape`). Not used: a capture
  holds a residual scale and a side table, which a listed primitive never has.
- **Morph scopes** (`SdfProgramBuilder.PushFieldMorph`). A blend of two fields
  by a lane, which is one number for the whole creation. Not a spatial weight.
- **Smooth blends and the influence walk** (`sdfComposeCandidate`,
  `SdfFieldEvaluator.ReadInfluences`). The third option above.
- **Sampled fields** (`SdfSampledField`). The fourth option above, and the
  path for painted or very numerous regions.
- **The relief's size rules** (`CreationNoiseDocument.DetailSpacing`,
  `WorldShapeDetail.PartsOf`, `WorldColliderSet.ReliefField`). Reused: the
  band joins the wavelength they already read.

### Open questions of the step back, and their owners

- **Is the narrowing acceptable?** The lead accepted it, and step A is built
  with it. A type that an author needs joins the list as one case.
- **What does a row cost the kernel?** *Owner:* step C.
- **Does the lattice's budget reach a band of a few millimetres?** *Owner:*
  step B, whose check reports the shortfall.
- **Does step 1's lane contract cover the four listed types?** Yes: a
  primitive row runs its type's shape row, and step A added the rows' own
  contract beside it, and the capsule's finite squared length.
- **Should the carrier change and the shared paint reader be done anyway?**
  *Owner:* the lead. They are refactors of four parallel lists and of two
  copies of one kernel reader, with no mask depending on them.
- **How do the shading octaves read the weight?** The typed masks can be
  packed for the shading stage as a paint is. *Owner:* the terrain plan's
  shading step, as before.
