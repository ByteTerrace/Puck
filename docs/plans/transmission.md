# Transmission and self-shadow: the nearest surface of another body

A design for the shadow term a wrapped material reads past the terminator,
written after three review rounds of illumination step 1 each fixed one
class of defect and exposed the same defect in a new shape, and revised
through two design reviews. It replaces the cascades' exit layer with a term
whose correctness rests on which body a surface belongs to, which the cascade
records exactly, and on nothing a mesh's topology or winding has to promise.
Implementation is a later lane; the steps at the end are its lanes, and the
remaining review mediums are their checks.

Status: design, final round. The code it describes is step 1's third round
(`passes/sdf-surface-stage.hlsli`, `SdfMeshRasterPass.cs`, the cascade's
exit phases in `SdfWorldPassRecorder` recorded through `RecordMeshPhase`
from the GPU selection, `WorldPlacementStamper.MeshOf`,
`SdfMeshDraw.EnclosesSolid`) and step 2's relight (`SdfMeshIrradiance.Relight`
and `Shade`, `SdfMeshProbeDepth.TraceHits`).

## The problem

Step 1 gave lit surfaces wrapped diffuse past the terminator,
`band(cosine) × visibility`, and had the surface pass query the cascades
wherever that response is positive ([decision](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)).
A receiver turned from the light must be shadowed by a real blocker (a roof,
a slab, a sheet) and must not be shadowed by the body it belongs to, whose
lit side stands between it and the light by construction. The rounds:

1. A thickness estimate from the surface's curvature. Flat geometry has no
   curvature, so a millimetre wing let a slab 10 cm off through and a
   centimetre sheet shadowed itself.
2. An exit layer per cascade: the casters re-rendered with their light-facing
   faces culled, so a receiver turned from the light compares against the
   first face where light *leaves* a solid. Review found the normal offset
   hid blockers, open casters vanished from the layer, and two-sided authored
   meshes self-shadowed.
3. The offset points straight at the light, non-enclosing casters go into the
   exit layer whole, and authored meshes are classified closed or open at
   load. Review found grazing self-shadowing from the receiver-plane clamp,
   one signed volume orienting multiple shells wrong, T-junctions turning
   solids into thin casters, and skinned deformation inverting winding while
   `EnclosesSolid` stays true.
4. This plan's first draft: two depth layers, the nearest surface and a peel
   of it, read by a per-mesh closed bit. Review found that a portal-clipped
   receiver whose entry is gone reads a sheet 2 m ahead as its own entry; a
   3 mm closed panel behind a sheet 1 m ahead loses the sheet because the
   second layer is the panel's own entry 12 mm before it, inside the bias; a
   peel tolerance merges a sheet half a millimetre from a box's front with the
   box; a triangle drawn in both windings passes edge pairing as closed; and a
   smaller clamp on the receiver-plane slope moves the self-shadow to a
   smaller angle.
5. The second draft, identity per texel: review found frame-local body
   indices compared against a held cascade from another frame;
   `MotionIdentity` both splitting one creation into groups and aliasing
   portal copies; the chord rule claimed beyond what two surfaces can tell; a
   bounded plane reference at grazing angles leaking a sheet a metre ahead;
   signed mean curvature as an uncertainty term; the GI's cached hits without
   a receiver identity and a ray origin that hit its own exit; and a peel
   footprint from the previous frame's receivers.

The exit layer read "the first face turned from the light" as "where light
leaves the first solid", which holds only for closed, consistently wound,
rigid, unclipped geometry. The first draft read "the second surface along the
light" as "the receiver's own exit", which holds only when nothing else lies
along the ray. Both read a property of *one* body out of a record that mixes
every body's surfaces. What the look asks for is a statement about identity:
light reaches a wrapped receiver's dark side through its own matter, and is
stopped by any other body. The cascade records which body each surface
belongs to, so the term asks that question directly, and the fifth round's
findings are about making that identity stable, canonical and honest about
what it can and cannot infer.

## What the look means

The term implements this meaning, which step 1 settled and this plan keeps:

- **Wrap is transmission through the receiver's own matter, independent of
  thickness.** A wrapped material's side turned from the light takes the
  light its wrap carries whether the body is a wing or a plate.
- **Any other body in front of the receiver shadows it**, however thin, at
  any distance: a roof, a slab, a sheet, a card, a chunk of a neighbouring
  union, a different placement of the same creation.
- **The receiver's own body shadows its dark side by its next surface along
  the light.** Light that has left the body once (through an arm, an eave, a
  wing over the torso, the near wall of a hollow box) and must enter it again
  is stopped. Without this, the arm's shadow on the torso would end at the
  terminator and the wrapped response, `wrap / (1 + wrap)` of the light there
  (0.26 at `wrap: 0.35`), would start as a hard seam under the arm. This rule
  keeps the shadow continuous across the terminator.

### What "next surface" can and cannot mean

Two winding-independent depth layers record surfaces, not occupancy. For
geometry contoured from the field, every surface is an occupancy transition
by construction (the contour is the boundary of the field's solid; shapes
that overlap are unioned before they are contoured), so "the body's next
surface along the light" is exactly "where the light next enters the body",
and the rule is the physical chord rule. A hollow baked shape (an onion) has
real inner surfaces: light leaves its outer wall into the cavity and enters
the inner wall, and the outer wall's dark side is a second chord, shadowed.
For an authored mesh the rule reads the same surfaces, and where the mesh's
components overlap or self-intersect (two boxes interpenetrating in one mesh,
a skin whose member pushes through its torso) a surface inside the union
counts as a chord that occupancy would not: the dark side behind it loses its
wrap. That is the accepted geometry: an authored mesh whose components do not
overlap, or whose author accepts the extra shadow where they do; the error is
one body over-shadowing itself and never a leak. The term does not infer
occupancy and the plan does not claim it does.

### A body

A body is what one placement draws as one solid, under one canonical key:

| Draw | Body key | Why |
|---|---|---|
| A static group and every chunk of it | the group (`SdfMeshPathBake` records chunk owners) | A slab whose lit face is in one chunk and dark face in another is one body |
| A rigid copy of a creation, every retained group of it, and its authored mesh | the copy's entity: `WorldRigidAssets`' `(target, address)` without the pose epoch | `MotionIdentity` today appends `group.Selector` and `"mesh"` to split one copy into motion histories (`SdfMeshPathBake.cs`), which is right for history and wrong for a body; and it keeps the pose-discontinuity epoch, which a teleport advances though the body is the same |
| A skinned creation with all its members | the copy's entity | One draw, one body |
| A portal-carried copy (`SdfSlotClip`, `Identity = Copy(Of)`) | `(the original's key, carry)` | The carried image is drawn elsewhere in the world; it is not the original's matter where the original stands. Today it inherits `MotionIdentity` through `with`, which the body key must not |
| A mirrored draw | its placement's key | Where it is drawn, it is one body |
| An authored prototype mesh on a placement | the placement's key | |

The key is a value the stampers supply on `SdfMeshDraw` (`Body: object`),
beside `Identity` (picking) and `MotionIdentity` (history), and it is neither:
`Identity` differs per chunk and per carried copy, `MotionIdentity` per group
and per epoch. Under V1 it becomes a field of the retained per-instance
record beside the instance's identity and generation
([V1](visibility.md#v1-retained-instances-ordered-compaction-and-geometry-level-buckets)),
and every instance of one body carries one value.

### The body index and its lifetime

The residency's tables (`SdfWorldTables`, one per residency) own one body
table: a map from body key to a **body index** with a **generation**, written
into every draw's record as one word, `SDF_MESH_BODY_WORD`: the index in the
low 24 bits (`SdfMeshRegion.MaxDraws` bounds it, since bodies never outnumber
draws) and the generation in the high 8. The cascades' fragment stage writes
that word into B1; the surface pass reads it from the hit draw's record and
compares the whole word. The rules:

- **One allocator per residency.** Every cascade and every surface pass of
  the residency's views draws from and reads this one table, so every
  comparison stays in its namespace. A nested view of another world is
  another residency with its own tables, cascades and surface pass, and no
  pass reads across.
- **An index is allocated when its body is first drawn and retired when the
  body has not been drawn for longer than any reader may hold a cascade or a
  history that names it.** The tables know the holds: the far cascade's
  (`SdfWorldPasses.Rates`, bounded by `SdfUpdateRateTiers.ShadowFarHertz`),
  a V5 static cache's, and V1's history by instance generation. Each hold
  records the frame it was drawn; an index freed at frame F is reusable only
  once every hold drawn at or before F has been redrawn. The far cascade's
  two layers are two holds: a split refresh draws its depth and its exit layer
  on consecutive frames (`SdfFarLayers`), so each layer records its own frame
  and the older bounds reuse. Until then a held cascade's B1 stays a true
  statement about the bodies it saw.
- **Reuse advances the generation**, so a word from a cascade or history
  older than the table accounts for can never equal a live body's word. The
  hold rule makes the mismatch impossible; the generation makes an
  unaccounted hold read "another body" (shadow) rather than "mine" (leak).
- **A held cascade needs no remap and no invalidation**, because its words
  stay valid; the far hold's conditions (`DecideFar`) gain nothing. A body
  table that must be rebuilt (a residency reset) invalidates every held
  cascade and history with it.
- Under V1, the body index composes with the stable instance identity and
  generation: the instance record carries it, the hold accounting reads the
  same frame numbers V1's history uses, and an instance that changes body
  (a prototype swap on one entity) is a new instance generation.

## The term

Each cascade holds, per texel, along the light:

- **L1**, the depth of the nearest surface of any facing, and **B1**, the body
  word of the draw that wrote it: today's depth pass with an `R32_UINT`
  colour attachment the fragment stage writes from the record. The stage
  keeps `[earlydepthstencil]`, so B1 is written by the fragment whose depth
  won, and L1 and B1 are one pair at every texel.
- **L2**, the depth of the nearest surface whose body word is not B1 or
  whose depth lies more than `ε` beyond L1: a peel of L1 keyed by identity.
  The peel draws the cascade's kept draws with no culling; its fragment
  stage loads L1 and B1 at its texel and discards a fragment of body B1
  within `ε` of L1. `ε` is in units of the stored depth, below, and never
  applies between two bodies.

A receiver `P` of body `b`, on triangle `T`, whose camera-side normal is
turned from the light reads, at a filter tap `t`:

- **B1(t) ≠ b**: another body is the nearest surface along that texel's ray,
  and it shadows `P` where it lies in front of `P`'s own surface there (L1
  compared as the lit side compares it). Nothing of `P`'s own body enters: not
  its thickness, entry, clip or winding.
- **B1(t) = b**: `P`'s own body is nearest there. If `b` is `Solid`
  ([below](#the-solid-bit)), the surface that can shadow `P` is L2(t): another
  body behind `b`'s entry, `b`'s own next surface in front of `P` (a second
  chord), or `P`'s own surface, which lies on `P`'s plane and reads lit. If `b`
  is not `Solid` (a sheet mesh), L1(t) is `P`'s own surface, lit, or another
  sheet of the same mesh in front, a blocker.

A receiver facing the light reads L1 at every tap and, when its material has
wrap, B1 as well (its grazing band carries `wrap / (1 + wrap)` of the light; a
plain material's cosine hides that band, and it reads no body). Whether each
tap can be placed against `P`'s surface at all is the
[resolution rule](#the-receiver-plane-and-the-unresolved-footprint); the
percentage-closer filter, the in-cone blocker search and the 4×4 views
average keep their shape with the body compatibility the views section adds.

### Why it is correct by construction

The cross-body term depends on one fact the cascade records exactly: which
body the nearest surface at a texel belongs to. It does not depend on
winding, closure, a cap, a determinant, a deformation, a level of detail or
a thickness:

- A **blocker** of any class, as the nearest surface at a tap, is a body
  other than `b` and shadows by depth alone. A blocker behind the receiver's
  own entry (B1 = b) is L2 for a `Solid` receiver, because `ε` never merges
  two bodies, and shadows by depth; for a sheet receiver its own surface is
  L1 and any blocker in front is B1 ≠ b.
- The **receiver's own body** never shadows its dark side through its first
  chord: with B1 = b and `Solid`, L2 at a tap on `P`'s surface is `P`'s own
  surface, and the resolution rule reads it lit where it can place it. Its
  next surface in front of `P` is a chord, a blocker.
- **Coincident surfaces of different bodies** are never merged: `ε` applies
  only to body B1's own surfaces. Only a chord of one body thinner than `ε`
  along the light collapses to one surface, which reads that chord's dark
  side lit.
- **A held cascade's words stay true** under the lifetime rule, and a stale
  word reads "another body".
- The only **inference** left, `Solid`, governs whether a body shadows its
  own dark side by its next surface (L2) or by any own surface in front (L1).
  A closed mesh wrongly read as a sheet reads its own entry as a blocker and
  loses its wrap; a sheet mesh wrongly read as `Solid` lets its own other
  sheets through to its dark side. Both errors are confined to one body
  shadowing itself. No cross-body shadow depends on the bit.

### The counterexamples

Both reviews' cases, and the term's answer to each. Depth is measured away
from the light.

| Case | Layers at the tap | Answer |
|---|---|---|
| Portal-clipped `Solid` body, entry clipped, receiver at 10 m, sheet at 8 m | L1 = sheet (B1 = sheet) | B1 ≠ b: shadowed by L1 |
| The same without the sheet | L1 = P (B1 = b), L2 behind | Own surface on the plane: lit |
| 3 mm closed panel at n·l = -0.25, sheet 1 m ahead | L1 = sheet | B1 ≠ b: shadowed; the panel's 12 mm chord never enters |
| Sheet at 0, thick box front at 0.5 mm, box back at 1 m (receiver) | L1 = sheet or box front (a tie); L2 = the other | B1 = sheet: shadowed by L1. B1 = box: L2 = sheet, another body, in front: shadowed |
| Triangle drawn as ABC and ACB, receiver on its back, sheet 1 m ahead | L1 = sheet | B1 ≠ b: shadowed, whatever the doubled triangle is classified (a sheet, by zero volume) |
| Thin foil blocker whose entry and exit fall within `ε` | L1 = foil (B1 = foil) | B1 ≠ b: shadowed |
| Arm (same body) over the torso's dark side | L1 = arm entry (B1 = b), L2 = arm exit | L2 in front of P: shadowed (a chord) |
| Hollow box, outer walls at 0 and 2, inner walls at 0.5 and 1.5, one body | L1 = 0, L2 = 0.5 | Receiver on the outer back at 2: L2 in front, shadowed (a second chord: light left the outer wall into the cavity). Receiver on the inner wall at 1.5 (facing the light): L1 in front, shadowed. The inner wall at 0.5 (turned from the light): L2 = itself, lit (wrap through the near wall) |
| Overlapping components [0, 2] and [1, 3] of one authored mesh, receiver at 3 | L1 = 0, L2 = 1 | Shadowed: the inner entry counts as a chord. The accepted over-shadow of overlapping authored components, above |
| Sheet A and the receiver's own entry renumbered between frames under a held far cascade | B1 holds A's word | The lifetime rule: A's index is not reused while the hold stands, and a stale generation reads "another body" |
| Stack of a body's own cards within `ε` | collapsed | The body's own stack reads lit on its dark side; another body's card is never merged |
| Inside a closed authored room | L1 = near wall outer, L2 = near wall inner | Receiver on the near wall's inner face: L2 = P, lit (wrap through the wall). On the far wall's inner face, facing the light: L1 in front, shadowed |
| Grazing plate at n·l = -0.005, nothing in front | L1 = own entry (B1 = b), L2 = own exit | Taps inside the plate's triangle resolve exactly; see the resolution rule |
| Grazing small panel behind a sheet, every tap outside its triangle | L1 = sheet, behind P in depth at the tap | No tap resolves: the receiver is unresolved and never reads lit; see the policy |

### `ε` and its envelope

The peel's tolerance exists for one purpose: the same triangle of body B1,
rasterized by the L1 pass and again by the peel pass, must be recognised as
the same surface. The requirement is **invariance**, not tolerance: both
pipelines use one vertex stage bytecode (`sdf-mesh-shadow.vert`), one
viewport and one rasterizer state; the position is computed `precise`, and
the SPIR-V position output carries the `Invariant` decoration, so a driver
compiles it once and interpolates it identically. `ε` is then `4 ulp` of the
stored `D32_FLOAT` depth at L1, compared in the stored encoding, a margin for
the rasterizer's interpolation alone, never a world constant and never a
cover for a different vertex compilation. No bound connects an alternate
compilation to four ulp, so none is allowed.

In metres, at the near half of a cascade's reversed-Z range where depth lies
in [0.5, 1), one ulp is `R · 2^-24` with `R` the cascade's depth range
`2 · radius + casters - 1` (`SdfMeshShadowCascades.Cascade`). With the
default coverage (16 m near, 120 m far, 200 m of casters) the near cascade's
`R` is about 231 m and the far cascade's about 439 m, so `ε` is 55 µm and
105 µm; an authored coverage of 20 km gives 4.8 mm. A chord of one body
thinner than `ε` along the light reads lit on its dark side; no other
surface, of any body, is affected.

The device witness, on both backends: one triangle drawn through the L1
pipeline and the peel pipeline leaves the peel target clear at every texel
the triangle covers, at world positions up to 10^4 m from the origin and at
1° and 89° to the light. A backend that fails it has a compile or state
defect to fix, not a tolerance to widen.

### The `Solid` bit

`SdfMesh.Encloses` becomes a `Solid` property: a body whose surfaces along any
ray come in entry and exit pairs, so its dark side is shadowed by its next
surface. `SdfMeshDraw.EnclosesSolid` is deleted: the determinant and
clip-plane tests served the exit layer and mean nothing here.

| Mesh | `Solid` | Why |
|---|---|---|
| Baked (`SdfMeshPathBake`), rigid or skinned, any level, any chunk | true | The contour winds the field's boundary; a skin whose member folds a chord through itself over-shadows that fold's dark side and shadows every other body exactly |
| Authored, closed by the test below | true | |
| Authored, open, non-manifold, or with a zero-volume component | false | A sheet, a soup, a doubled triangle, a mesh with an internal wall |
| Authored with `solid: true` or `solid: false` on its `mesh` | as written | The author's word outranks the test |

The test is geometric, so two spellings of one surface classify alike: weld
vertices at equal positions; split every edge at a vertex that lies on its
interior within `10^-5` of that edge's length, found through a spatial hash of
edge boxes (linear in edges and vertices, never the pairwise scan); require
every undirected edge to belong to exactly two triangles (winding does not
enter: a box with one face reversed is the same closed surface); orient each
edge-connected component consistently by walking its triangles; and require
each component's volume to be non-zero. A mesh passes when every component
passes. Winding, triangulation and vertex duplication are spellings the test
ignores.

What the test does not promise, and what the author's word is for:

- **Features thinner than the split tolerance.** A closed box 100 m long and
  0.1 mm thick has a split tolerance of 1 mm on its long edges, so its two
  faces can be read as one surface; the test errs toward a sheet and the
  author writes `solid: true`.
- **Non-manifold closed meshes** (an edge shared by three or more triangles:
  a box with a partition attached along its edges). "Solid" is a choice about
  which surfaces pair, which the geometry does not make.
- **Shells that cross through triangle interiors without shared vertices.**
  The test sees two closed components and says solid; the rule then reads the
  inner shell's surfaces as chords, the accepted over-shadow above. A
  partition that crosses a face's interior rather than meeting its edges
  classifies as this case, not as the non-manifold one; the author who wants
  it read as one solid gets that already, and one who wants the partition to
  shadow writes nothing.
- **Gaps wider than exact equality**: not closed as authored. The author
  closes it or declares it.
- A closed mesh the author wants to **self-shadow with every own surface**
  (`solid: false`).

`WorldPrototypeMesh` (`src/Puck.World.Schema/WorldPlacement.cs`) gains
`Solid: bool?`; `puck schema` carries it; `puck lint` reports a `solid: true`
whose mesh fails the test, naming the first unpaired edge or zero-volume
component. Authored meshes return to two-sided camera drawing for every mesh
(`TwoSided`): no orientation code decides which faces the camera sees, so a
misoriented shell never loses its walls. The cascades draw the authored
winding only (the first half of the two-sided index list, whose count
`SdfMesh` records), so a cascade rasterizes each authored triangle once and
the peel meets no authored duplicate. `Mirrored` stays as the camera's
concern for single-sided baked meshes; `ClosedOutward`'s volume-sign flip is
deleted.

### The receiver-plane and the unresolved footprint

The receiver-plane comparison (`sdfMeshShadowGap`: each tap compares against
the receiver's plane extended to the tap's texel centre) is kept, and its
clamp (`lightNormal.z` floored at 0.2) is removed outright, not lowered: a
floor of 0.02 moves the self-shadow from n·l = -0.05 to -0.005 rather than
removing it. The gradient `g` (depth per texel along the cascade's axes) is
exact for the triangle the cascade rasterized, with a `10^-6` floor on `|n·l|`
against division by zero.

An exact plane is exact only where `P`'s own surface is that plane. Past
`T`'s edges the surface is a neighbouring triangle, past the body's silhouette
it is something else, and a bounded extrapolation is worse than either: a
grazing panel with every tap rounded into one texel column 0.45 texels along
`g` has its surface 90 texels of depth away there, and a sheet a metre in
front of it, sloping with it, is *behind* `P` in depth at that column. The
plane says the sheet is a metre in front of the panel there, which is true;
a reference bounded to `P ± S` says it is behind, which leaks. So the rule
does not bound the plane; it decides, per tap, whether the plane can be
trusted, and what to do when it cannot.

**Resolution of a tap.** `delta` is the exact offset from `P`'s projection to
the tap texel's centre, `d` its length in metres, `n_z = |n · f|` the
receiver's facing.

- **Inside**: the tap texel's centre lies inside `T`'s projection onto the
  cascade plane (a 2D point-in-triangle test on the three vertices the
  surface pass already holds for its barycentrics). `P`'s surface at the tap
  is exactly `plane(t) = P + g · delta`; the tolerance is `u0`, a few ulp of
  depth noise. This is exact at any angle, which is round 3's planar high and
  the grazing panel above (a merged planar face is one polygon, so a plate's
  taps are inside it).
- **Near**: outside `T`, but the surface's deviation from `T`'s plane within
  `d` is bounded by
  `u1 = (d · tan θ_T + e_ℓ) / n_z ≤ S · texel`, where `θ_T` is the largest
  angle between `T`'s face normal and its three vertex normals, `e_ℓ` the
  recorded collapse error of the level the cascade drew (`SdfMesh.Levels`),
  and `S` the resolvable span in texels (4 at first; the lane measures). `u1`
  is non-negative, a bound rather than an estimate, and its assumptions are
  explicit: the surface turns within one tap's distance of `T` by no more
  than it turns within `T` (a baked mesh's vertex normals are the field's
  gradients and `SplitAtCreases` gives a crease its own normals, so a smooth
  surface satisfies it and a crease fails it on the far side, where the
  neighbouring face reads as another surface); the division by `n_z` projects
  a displacement along the normal onto the light's depth axis; the cascade's
  level differs from the camera's by at most `e_ℓ`. A mesh without normals
  has `θ_T = 0` and only its own triangle resolves.
- **Far**: neither. The tap's stored surface cannot be placed against `P`'s
  own surface.

**Per tap.**

- B1(t) ≠ b, Inside or Near: occluded where `L1(t) < plane(t) - u`, lit
  otherwise; known. The other body is in front of `P`'s surface at that tap
  or it is not, exactly.
- B1(t) ≠ b, Far: occluded where L1(t) lies inside the light's cone over `P`
  (the existing `gap.y` test against `P`'s own depth: another body directly
  in front of `P` shadows it whatever `P`'s surface does elsewhere); excluded
  otherwise, since a surface behind `P` at a far tap says nothing about `P`'s
  surface there.
- B1(t) = b, Inside or Near: `L(t)` is L2 for a `Solid` body, L1 otherwise.
  `|L(t) - plane(t)| ≤ u`: `P`'s own surface, lit. `L(t) < plane(t) - u`: a
  chord or another body behind `b`'s entry, occluded. Otherwise lit. Known.
- B1(t) = b, Far: excluded. Without a bound, `L2` in front of `P` is as likely
  `P`'s own exit curving toward the light as a second chord, and the term
  does not guess.

**The receiver.** Known taps average as today, with the soft ramps and the
in-cone blocker search sizing the filter from the nearest known occluder. A
receiver with **no known tap is unresolved**: its slot takes a reserved code,
`SDF_SHADOW_UNRESOLVED`, beside `SDF_SHADOW_UNQUERIED`. The views filter
leaves it out of its neighbours' averages as it leaves out an unqueried slot,
and fills it from the measured, body-compatible neighbours in its window;
where the window holds none, the code decodes as **zero**, shadow. An
unresolved receiver therefore never reads a distant blocker absent. Its look
cost is a dark band where a wrapped body's terminator is unresolved across a
whole 4×4 window: for a smooth baked body that is the band where
`tan θ_T / n_z > S` at one texel's distance, about 2.5° of surface at
`θ_T = 10°`, which on the Moth is under a millimetre and fills from its
neighbours; for a faceted authored mesh without normals, the band is wherever
its triangles are smaller than the filter and it turns from the light, and it
reads dark. Erring toward shadow there is the honest answer, and the owner
chooses it [below](#the-owners-calls).

**Footprint.** A tap's offset from the disk pattern has its component along
`ĝ` clamped to `± S / |g|` before rounding. This keeps taps Inside or Near as
the receiver grazes and costs nothing in correctness; a tap the rounding
carries past `S` is simply Far.

The toward-light offset (`NormalOffsetTexels`), the constant bias
(`ConstantBias`) and the isotropic slope allowance (`SdfMeshShadowSlopeBias`)
covered a plane that was not exact and taps that could not tell the
receiver's surface from a blocker; `u` and the resolution rule take their
place. The first review's 6 cm slab exit over a panel at n·l = -0.25, lost
today because 4.3 cm of offset and bias plus 2.5 texels of allowance exceed
the gap at every tap, reads `gap ≈ 0.06 - u0` at Inside taps and is shadowed.

The floors, as what the cascade cannot resolve:

- **Near contact.** Another body within `u + soft` of the receiver's surface
  along the light reads by the soft ramp (`SdfMeshShadowSoftTexels`, one
  texel: 1.6 cm at the near cascade at Full, 11.7 cm at the far).
- **Unresolved receivers**, above, read their neighbours or shadow.
- **Own chord thinner than `ε`** reads lit on its dark side.
- **Concave creases of a wrapped dark side**: the face across the crease
  lies in front of `P`'s plane and reads as a chord, so the crease darkens as
  ambient occlusion already darkens it.

None of these is a leak of a blocker at a distance.

## What each caster class gets

| Caster class | As a blocker | As a wrapped receiver |
|---|---|---|
| Baked SDF creation, static | Its surfaces carry its body; the nearest shadows any other body behind it | `Solid`; its own surface resolves on its plane; any other body in front shadows it |
| Baked, skinned or deforming | Every drawn surface counts, however the members move | `Solid`; a member pushed through another over-shadows there, never leaks |
| Baked, chunked or at a coarser level | Chunks share the body; collapsed folds are surfaces; `e_ℓ` enters `u1` | As the static case; a chord across a chunk seam is one body |
| Baked hollow (an onion) | Entry and exit of each wall | `Solid`; the outer wall's dark side is a second chord, shadowed |
| Authored, closed by the test | Entry and exit count, winding unread | `Solid` |
| Authored, multi-shell, nested | Each shell's surfaces count | `Solid` if every shell has volume; the inner shell is a chord for the outer back; camera sees every wall |
| Authored, overlapping components | Count | `Solid`; over-shadowed where components overlap |
| Authored, T-junctioned closed | Counts whole | `Solid`: the test splits the junction |
| Authored, open card, two-sided card, foliage card | One surface, drawn once | A sheet: shadowed by anything of another body in front and by its own other cards; lit alone |
| Authored without normals | Counts | Resolves only inside its own triangle; unresolved taps fill from neighbours or read dark |
| Portal-clipped body | Its remaining surfaces carry its body | A sheet ahead is B1 ≠ b, shadowed; alone, its own surface reads lit |
| Portal-carried copy | Its surfaces carry the copy's key | Its own body; the original is another body |
| Mirrored draw | Its surfaces where drawn | Its mesh's `Solid`; no determinant test |
| Baked thin foliage (closed slabs) | Entry and exit; thinner than `ε` along the light, one surface | `Solid`; another placement's leaf is another body |
| Glass | Skipped by the cascades, as today | — |

## Cost: a measurement plan

What is known at the stage on Direct3D 12 (`world.counters gpu pass
sdf.world$shadow-near`): the cascade's two render passes both draw the same
kept set, so the cascade's vertices are twice each pass's (the selection
kernel issues both as indirect draws, which changes the recording and not the
work); front-face culling discards after the vertex stage and saves
fragments, not vertices. The term's second pass costs the
same vertex work and the depth pass's fragments, each running two loads and a
compare, and the first pass gains a colour write. Up to three cascades can
refresh in one frame.

The exit layer's baseline, read at four standard stops on both backends with
the day cycle running: on the RTX 4070 the three cascades together already
reach or pass the RTX 2060's 1.5 ms budget below at every stop but the Vulkan
rim, and the `surface` pass costs under a millisecond.
At the stage the far cascade draws five times the near cascade's casters for
2.4 times its vertices; at the meadow every cascade draws the blades, about
8,500 draws against the mesh pass's 3,846. Each cascade issues three indirect
draws, and the two render passes of a cascade are in its counts. The redesign
is read against that baseline, re-taken at the start of the work, with the 2060
and the Deck. The plan:

- **Devices and tiers.** The bar is the highest quality on every bar device
  ([the register](../decisions/rendering.md#the-pipeline-foundation)), so the
  Steam Deck is measured at **Full** (2048 texels), as the RTX 2060 and the
  RTX 4070 are. A Half reading on the Deck is information about the tier, not
  qualification. Both backends on the desktop cards.
- **Stops.** `debug.go stage` (the step 1 witness), the meadow (the most
  wrapped receivers and the most casters), `debug.go portals` (nested views
  each with cascades), `valley-rim` (the far cascade's draw count).
- **Readings.** `world.gpu-timing` for `shadow-near`, `shadow-far`,
  `shadow-second` and `surface`; `world.counters gpu pass sdf.world$shadow-*`
  for draws, vertices and fragments; owned bytes for the attachments, per
  view and in total at the portal stop. Each stop before and after, with
  `world.cadence off` and `world.rates off`, read twice and the second kept.
- **Budgets.** RTX 2060 at Full: the three cascades together within 1.5 ms
  and the surface pass within 0.3 ms of today's reading, inside the 16.7 ms
  frame. Steam Deck at Full: the cascades together within 3.5 ms inside the
  22.2 ms frame. Live bytes at the portal stop: the three attachments across
  every live view within the frame's accounted GPU memory with the same
  headroom the stop has today; the remedy if not is transient aliasing of
  the peel and body images across views the graph records in sequence, which
  never live at once. A budget missed is fixed at its cause, never by
  restoring the exit layer (a term that leaks is not a baseline), and the
  remedies are below.
- **Two peel implementations, one output.** (a) A depth-tested raster with a
  discard-only fragment stage and no counter: the driver may test depth early
  with the write deferred, but MJP measured on Direct3D 12 that a `discard`
  in the instruction stream with depth writes enabled still ran 582 k pixel
  invocations against 441 k without it on an RX 7900 XT unless draws were
  serialised (<https://therealmjp.github.io/posts/to-earlyz-or-not-to-earlyz/>),
  so its overhead is bounded by one depth pass's overdraw and measured.
  (b) A raster with no depth attachment whose fragment stage computes its
  reversed depth from `SV_Position.z`, applies the identity test, and
  `InterlockedMax`es its bits into an `R32_UINT` L2 image: the stored depth
  is a positive reversed-Z float whose bits order as unsigned integers, so
  **max** keeps the nearest surface, as the attachment's `Greater` test does,
  and the image clears to zero, the far plane, as the attachment does. The
  image is cleared each frame, transitioned to storage for the peel and to
  sampled for the surface pass. Both variants store identical bits for
  identical fragments (one vertex stage, one interpolation), and the surface
  pass loads either as a float. The lane keeps whichever is cheaper on the
  RTX 2060 and the Steam Deck, which may differ.
- **Counters.** The L1 pass keeps `[earlydepthstencil]` and
  `puckCountFragmentWork`. The discard peel counts no fragments (a UAV write
  disables early rejection, the stated reason for the HLSL early-pixel-culling
  opt-in, <https://microsoft.github.io/hlsl-specs/proposals/0053-allow-early-pixel-culling/>);
  the atomic peel counts every fragment it runs. `gpu.draws` and
  `gpu.vertices` count both as any draw.
- **Storage.** Per cascade at Full: L1 `D32` 16 MiB, B1 `R32_UINT` 16 MiB,
  L2 `D32` or `R32_UINT` 16 MiB: 48 MiB, against 32 MiB today; three cascades
  144 MiB against 96 MiB per full-resolution view. The body attachment stays
  `R32_UINT`: a narrower one would need a per-frame code table, automatic
  widening past its cardinality and a held-cascade code lifetime, none of
  which is worth designing before a measurement asks for it.
- **The surface pass.** A wrapped receiver reads B1 at every tap (28 per
  cascade) and L2 at the taps where B1 = b, plus the point-in-triangle test.
  Plain materials read no body. The meadow is the worst case.
- **Confining the peel**, behind measurement: L2 is read only at taps where
  B1 is a `Solid` body with a wrapped material, so a tile mask built from the
  **current frame's** final B1 (tiles holding such a body) bounds the peel's
  draw area conservatively for the frame it shades. No footprint from a
  previous frame's receivers is used: a camera cut, a portal, a material edit
  or a disocclusion can demand texels the previous frame did not.
- **A world with no wrapped material** records no peel and writes no body
  attachment: the program's material table knows its wraps
  (`m_wrapSignature` already keys the surface pass).

## Composition

### V5 caster culling and the two-phase update

([visibility](visibility.md#v5-multi-view-and-shadow-work).) The L1 and B1
pass may run in two phases as the mesh pass does: phase one draws the
previous frame's visible set, the light-space occlusion kernel builds a
pyramid of L1 and tests the kept boxes, phase two draws the newly visible.
The peel runs **once, after L1 and B1 are final, over every box-kept draw**.
It cannot run in phases: a sheet phase two finds closer than phase one's L1
would make the old L1 the correct L2, and no redraw of the new sheet can
promote it. A draw may be dropped from the L1 phases when it lies wholly
behind L1's pyramid (it cannot be L1), but never from the peel (it may be
L2), and no prior frame's L2 may cull the peel. The receiver mask and the box
cull apply to both passes alike. Static cascade caching, if measured
worthwhile, caches L1, B1 and L2 together and is a hold the body table
accounts for.

### The views filter

`sdfViewsFilter` averages each light's visibility over a 4×4 window of
pixels within two footprints of the pixel's tangent plane and within 60° of
its normal. Two adjacent coplanar receivers of different bodies under one
slab now carry different predicates: the slab's owner reads its own
transmission, its neighbour reads the slab's shadow, and an average across
the seam would brighten one and darken the other. The gather therefore
carries each pixel's body word beside its shadow word, and a slot's average
takes only pixels of the pixel's own body (the same word), with the plane and
normal tests as today. An unresolved slot fills from those same pixels. A
window with no compatible measured pixel decodes unqueried as one and
unresolved as zero.

### GI

The irradiance volume reads no camera cascade. Today `SdfMeshIrradiance.Shade`
applies the material's wrap to a hit's Lambert term and casts the sun's
shadow ray from the hit lifted a millimetre along its normal
(`scene.Unblocked`), so a dark-side hit's ray first meets its own exit and
its wrap contributes no bounce: conservative, no leak. Under the term:

- **The cached hit carries its receiver.** `SdfMeshProbeDepth.TraceHits`
  writes two words per texel (a signed distance; a packed normal and
  material); it writes a third, the hit's **scene instance** (the index into
  `SdfMeshIrradianceScene`'s instance list) and triangle. A probe's hits are
  already tagged with the scene geometry version (`m_geometry`) and retraced
  when a build changes their region, so the handle is valid for exactly the
  scene the hits came from; the scene resolves an instance to its draw's body
  key and `Solid`.
- **The sun ray starts on the surface and excludes its own triangle.** The
  relight casts from the hit point itself, not lifted, toward the sun, and
  the scene's traversal skips the originating (instance, triangle), so the
  starting surface is never a hit. Hits within `ε_r` of one another on one
  instance (a ray grazing a shared edge, a duplicate at a seam) merge into
  one, `ε_r = max(10^-4 m, 2^-20 · |hit position|)`, a positional precision
  bound in ray space; it has nothing to do with the cascades' `ε`.
- **The rule on the ordered hits** `h1 … hk` from the surface toward the sun,
  within the lighting's reach: no hit, lit (the lit side in the open, and a
  sheet's dark side alone); `hk` of another body, shadowed; `hk` of the
  receiver's body and the receiver not `Solid`, shadowed (an own sheet in
  front); otherwise drop `hk` and any merged duplicates of it and, if a hit
  remains, shadowed (a chord or another body behind the entry), else lit.
  The witness: an unobstructed slab's dark side at n·l = -0.25 casts through
  its own body and meets its entry from inside, `hk` is its own, nothing
  remains, lit, and its wrap bounces; a sheet above it is `hk`, shadowed,
  nothing bounces. A lit-side hit's ray leaves at once: no hit, lit; a blocker,
  shadowed; its own arm, two own hits, the entry dropped and the exit
  remaining, shadowed.
- The GPU relight of illumination's decision 3 carries the same three words
  per hit through the trace-hits interface and applies the same rule; a
  light-space depth over the volume is not used.

### The rest

- **Cel shading.** `band(cosine) × visibility` is unchanged; this design
  supplies the visibility.
- **The far cascade's hold** (`SdfUpdateRates.ShadowFar`): L1, B1 and L2 are
  drawn and held together under the held camera, and the body table holds
  their indices.
- **Portal and mirror views.** Each nested view fits its own cascades and
  runs the same passes against its residency's body table.

## Rejected

- **Depth order alone** (the first draft). Two depth layers cannot say which
  body a surface belongs to, and the closed bit's inference is not
  one-directional.
- **Frame-local body indices** (the second draft). A held cascade from
  another frame compares stale words; the lifetime rule replaces them.
- **`MotionIdentity` as the body** (the second draft). It splits a copy into
  groups and aliases carried copies.
- **A bounded plane reference at grazing angles** (the second draft). It
  leaks a sloping sheet a metre ahead; the resolution rule replaces it.
- **The exit layer** (rounds 2 and 3). Sound only when every drawn face's
  winding says which side the light is on; not a fallback.
- **A body rule without the next-surface rule** (own matter never shadows
  own dark side). One layer and one id would suffice, and the arm's shadow on
  the torso would end at the terminator as a seam.
- **Inferring occupancy from two surfaces.** It cannot be done without
  winding; the rule reads the next surface and says so.
- **Thickness baked from the field.** The chord through a body depends on
  the light's direction; a scalar follows it only at normal incidence; a
  directional basis multiplies vertex words; neither exists for an authored
  mesh or follows a skin's members (Barré-Brisebois and Bouchard call their
  baked thickness an approximation).
- **Field visibility for SDF creations.** A second `mapCore` call site in a
  pixel kernel.
- **Shadow-map thickness as transmittance** (Jimenez et al.; Frostbite). A
  different look: a thick body's dark side goes dark.
- **Ray-traced shadows.** The Steam Deck carries the baseline without them.
- **Stencil shadow volumes.** Orientation-dependent and fill-bound.

## Implementation steps

Each is one lane with a mechanical check. The first two are serial; the
others run beside the second. The remaining review mediums are checks here.

1. **`transmit-identity`: the body key, the body table, the two layers and
   the format path.** the draw's `Body` supplied by the stampers and the bake
   per the [key table](#a-body); the tables' body table with its lifetime
   accounting and `SDF_MESH_BODY_WORD`; an `R32Uint` member of `GpuPixelFormat` with its
   texel size, both format adapters (`DirectXGpuFormats`,
   `VulkanGpuFormats`), an integer clear value on `GpuColorAttachment`, the
   cascade render pass gaining a colour attachment, the package's storages and
   kernel interface members for B1 and the retyped L2, the pass recorder's
   bindings and layout transitions, and the `uint` fragment output; the L1
   stage writes B1 under `[earlydepthstencil]`; `ShadowExitPipeline` becomes
   the peel (both variants behind one description, a lever for the
   measurement); the cascade's two exit phases (the selection kernel's
   enclosing and non-enclosing classes, `sdf-mesh-selection.hlsli`) become
   one `RecordMeshPhase` over the cascade's whole selected set through the
   peel pipeline after L1 and B1 are final, and the enclosing class leaves
   the selection; `sdfMeshShadowDepth` reads L1, B1 and L2 with one
   integer texel coordinate and no filtering; `sdfMeshShadow` applies the
   term's layer choice. `SdfMeshDraw.EnclosesSolid` is deleted and
   `SdfMesh.Encloses` renamed `Solid`. A palette with no positive wrap records
   neither the attachment nor the peel.
   Checks: the `ε` device witness on both backends over its envelope; a
   law that the body table never reuses an index while the far cascade holds
   and that a copy's groups, chunks and authored mesh share one word while a
   carried copy does not; the atomic variant's L2 bits equal the raster
   variant's at every texel on one scene; settled captures on both backends
   at the plinth stop (the Moth's cheek under the slab dark, in the open lit)
   and at `debug.go portals` with the avatar straddling the glass (its dark
   side keeps wrap, and a sheet between it and the sun shadows it); the far
   cascade held across a draw-list change (a bake finishing) leaves a
   wrapped receiver's shadow unchanged; `world.counters gpu pass
   sdf.world$shadow-near` shows one peel pass; the measurement plan's
   readings recorded here, the Deck at Full among them.
2. **`transmit-plane`: the resolution rule.** The footprint clamp, the exact
   gradient, Inside by point-in-triangle, Near by `u1`, Far, the per-tap
   decisions, `SDF_SHADOW_UNRESOLVED` through the record and the views filter
   with the body-compatible average; the offsets, constant bias and slope
   allowance deleted; `S` and `u0` set by measurement.
   Checks: `world.debug-view shadow` on a flat wrapped plate at n·l = -0.005
   and -0.05 in the open reads 1 across it on both backends; the 6 cm slab
   exit over the panel shadows it; the grazing panel behind a sloping sheet a
   metre ahead reads shadowed, inside its triangle and, as a small panel,
   unresolved and dark; the Moth's cheek shows no band at its terminator under
   `world.toon off` and a presented burst shows no flicker there; two
   coplanar wrapped slabs of different bodies under one roof keep their own
   visibilities across their seam.
3. **`transmit-authored`: authored meshes.** `MeshOf` draws every authored
   mesh two-sided and records the cascade's single-winding count; the closure
   test with its spatial hash replaces `ClosedOutward`; a `Solid` on `WorldPrototypeMesh`;
   `puck schema`; the lint report.
   Checks: a scratch world with (a) two shells in one mesh, outward and
   inward wound, both showing every wall, the outer back shadowed by the inner
   shell; (b) a T-junctioned closed box with wrap keeping its far side's wrap
   undeclared, and the same box with one face reversed; (c) a wrapped open
   card behind a card of another placement, shadowed, and behind a card of its
   own mesh, shadowed; (d) a doubled triangle classified a sheet; (e) a box
   with an edge-attached partition classified a sheet, and `solid: true`
   making it solid; (f) a 100 m by 0.1 mm closed box classified a sheet and
   `solid: true` restoring it; (g) a 100,000-triangle mesh classified in
   under 100 ms at load.
4. **`transmit-gi`: the rule on the trace.** The third hit word;
   `SdfMeshIrradianceScene` resolving an instance to body and `Solid` and
   skipping an originating triangle; `Shade`'s sun ray from the surface with
   merged duplicates and the ordered rule.
   Checks: the CPU irradiance law with a wrapped slab under the sun and a
   sheet above it: the slab's dark side bounces nothing; the sheet removed,
   it bounces its wrap; a hit on a lit face under its own arm bounces nothing;
   a probe retraced after a build keeps valid handles.
5. **`transmit-docs`**, folded into whichever lane lands last: the
   [decision](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)
   rewritten for the term, the handbook's
   [shadow passage](../rendering/sdf/handbook/lighting-and-shading.md), the
   rendering skill's soft-shadow bullet, the V5 note in
   [visibility](visibility.md#v5-multi-view-and-shadow-work), the mesh GI
   reference, and the packet.

## The owner's calls

- **A body's own overhangs and hollows shadow its dark side** (the
  next-surface rule): the Moth's wing shadows her torso's dark side as it
  shadows her lit side, a hollow box's outer back is dark, and overlapping
  components of one authored mesh over-shadow each other. The alternative
  (own matter never shadows own dark side) is one layer cheaper and shows a
  seam of `wrap / (1 + wrap)` at the terminator under every overhang.
- **An unresolved receiver reads its neighbours, or shadow.** Where a
  wrapped body's terminator cannot be placed against the cascade across a
  whole 4×4 window, it reads dark rather than lit. On smooth baked bodies the
  band is under a millimetre and fills from neighbours; on authored meshes
  without normals it is wherever triangles are smaller than the filter, and
  it reads dark. The alternative, reading lit, is the leak every round has
  chased.
- **Wrap stays thickness-independent.** A physically attenuated transmission
  is a different look and could be a separate authored material term later.
- **`solid: true | false` is author-facing vocabulary** on a prototype's
  `mesh`, for the geometry the test cannot settle, listed above.

## References

What each supports, and nothing more:

- Wang and Molnar, "Second-Depth Shadow Mapping", UNC-CS TR94-019, 1994,
  <https://www.cs.unc.edu/techreports/94-019.pdf>: the mechanism of a
  second-depth layer per light texel. Its correctness argument rests on the
  second surface being dark, which Puck's wrap denies.
- Everitt, "Interactive Order-Independent Transparency", NVIDIA, 2001,
  <https://developer.download.nvidia.com/assets/gamedev/docs/OrderIndependentTransparency.pdf>:
  depth peeling as a raster pass that discards against a previous layer.
- Bavoil and Myers, "Order Independent Transparency with Dual Depth
  Peeling", NVIDIA, 2008,
  <https://developer.download.nvidia.com/SDK/10/opengl/src/dual_depth_peeling/doc/DualDepthPeeling.pdf>:
  that a min/max blend yields the nearest and farthest layers, not the
  second-nearest.
- Isidoro, "Shadow Mapping: GPU-based Tips and Techniques", GDC 2006,
  <https://gdcvault.com/play/1013442/Shadow-Mapping-Tricks-and>: the
  receiver-plane depth bias, which `sdfMeshShadowGap` implements with the
  face normal in place of screen derivatives.
- MJP, "To Early-Z, or Not To Early-Z",
  <https://therealmjp.github.io/posts/to-earlyz-or-not-to-earlyz/>: the
  measured invocation counts with and without `discard` under depth writes on
  Direct3D 12, and that UAV writes force the late test by default.
- Microsoft, HLSL proposal 0053 "Allow early pixel culling",
  <https://microsoft.github.io/hlsl-specs/proposals/0053-allow-early-pixel-culling/>:
  that UAV access in a pixel shader disables early depth and stencil
  optimisation today and that the opt-in is under consideration.
- Microsoft, "Hardware support for Direct3D 12.0 formats",
  <https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/hardware-support-for-direct3d-12-0-formats>,
  and Khronos, the Vulkan specification's mandatory 32-bit format table,
  <https://docs.vulkan.org/spec/latest/chapters/formats.html#formats-mandatory-features-32bit>:
  `R32_UINT` as a render target and point-loaded image on every device of
  either baseline, without filtering or blending, which the term never uses.
- Jacobson, Kavan and Sorkine-Hornung, "Robust Inside-Outside Segmentation
  using Generalized Winding Numbers", SIGGRAPH 2013,
  <https://users.cs.utah.edu/~ladislav/jacobson13robust/jacobson13robust.pdf>:
  the state of the art for inside-outside classification of imperfect
  meshes, which the term does not need. Takayama, Jacobson, Kavan and
  Sorkine-Hornung, 2014, <https://arxiv.org/pdf/1406.5431>: the limits of
  that method's orientation step, not a theorem about orientation.
- Barré-Brisebois and Bouchard, "Approximating Translucency for a Fast,
  Cheap and Convincing Subsurface Scattering Look", GDC 2011,
  <https://frostbite.com/frostbite/news/approximating-translucency-for-a-fast-cheap-and-convincing-subsurface-scattering-look>:
  baked per-vertex thickness as an approximation, rejected above.
- Jimenez, Whelan, Sundstedt and Gutierrez, "Real-Time Realistic Skin
  Translucency", IEEE CG&A 30(4), 2010,
  <https://graphics.unizar.es/papers/Real-Time-Realistic-Skin-Translucency.pdf>:
  shadow-map thickness as the transmittance distance, the rejected look.
- Haar and Aaltonen, "GPU-Driven Rendering Pipelines", SIGGRAPH 2015: the
  light-space caster cull V5 adopts, cited in [visibility](visibility.md).

---

[Plans](README.md) · [Illumination](illumination.md) · [Visibility](visibility.md) · [Decisions](../decisions/rendering.md)
