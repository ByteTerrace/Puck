# Transmission and self-shadow: the nearest surface of another body

A design for the shadow term a wrapped material reads past the terminator,
written after three review rounds of illumination step 1 each fixed one
class of defect and exposed the same defect in a new shape, and revised after
a design review of its first draft showed that depth order alone repeats the
pattern. It replaces the cascades' exit layer with a term whose correctness
rests on which body a surface belongs to, which the cascade can record
exactly, and on nothing a mesh's topology or winding has to promise.
Implementation is a later lane; the steps at the end are its lanes.

Status: design, unimplemented. The code it describes is step 1's third round
(`passes/sdf-surface-stage.hlsli`, `SdfMeshRasterPass.cs`,
`SdfWorldPassRecorder.RecordExitDraws`, `WorldPlacementStamper.MeshOf`,
`SdfMeshDraw.EnclosesSolid`, `SdfMeshIrradiance.Shade`).

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
   box; a triangle drawn in both windings passes edge pairing as closed; a
   smaller clamp on the receiver-plane slope moves the self-shadow to a
   smaller angle; and the GI's probe hits lie outside the camera's cascades
   and their caster cull.

The exit layer read "the first face turned from the light" as "where light
leaves the first solid", which holds only for closed, consistently wound,
rigid, unclipped geometry. The first draft read "the second surface along the
light" as "the receiver's own exit", which holds only when nothing else lies
along the ray. Both read a property of *one* body out of a record that mixes
every body's surfaces. What the look asks for is a statement about identity:
light reaches a wrapped receiver's dark side through its own matter, and is
stopped by any other body. The cascade can record which body each surface
belongs to, so the term below asks that question directly.

## What the look means

The term implements this meaning, which step 1 settled and this plan keeps:

- **Wrap is transmission through the receiver's own matter, independent of
  thickness.** A wrapped material's side turned from the light takes the
  light its wrap carries whether the body is a wing or a plate.
- **Any other body in front of the receiver shadows it**, however thin, at
  any distance: a roof, a slab, a sheet, a card, a chunk of a neighbouring
  union, a different placement of the same creation.
- **The receiver's own body shadows its dark side only by the chord rule**:
  light that has already left the body once (through an arm, an eave, a wing
  over the torso) and must enter it again is stopped. Without this, the
  arm's shadow on the torso would end at the terminator and the wrapped
  response, `wrap / (1 + wrap)` of the light there (0.26 at `wrap: 0.35`),
  would start as a hard seam under the arm. The chord rule keeps the shadow
  continuous across the terminator: the lit side sees the arm as a blocker,
  the dark side sees it as a second chord.

**A body** is what one placement draws as one solid: a baked group and every
chunk of it (`SdfMeshPathBake` emits chunks as separate draws of one group),
a rigid copy of a creation (each copy a body of its own, as
`SdfMeshDraw.MotionIdentity` already names it), a skinned creation with all
its members, one authored mesh, a mirrored copy, a copy drawn through a
portal pair. The frame's tables assign each body a compact index for the
frame and write it to the draw's record (`SDF_MESH_BODY_WORD`); every draw of
the same body carries the same index, so a slab whose lit face lies in one
chunk and dark face in another is one body.

## The term

Each cascade holds, per texel, along the light:

- **L1**, the depth of the nearest surface of any facing, and **B1**, the
  body it belongs to: today's depth pass with a colour attachment
  (`R32_UINT`) the fragment stage writes from the draw's body word.
- **L2**, the depth of the nearest surface that is not a surface of body B1
  within `ε` of L1: a peel of L1 keyed by identity. The peel pass draws the
  cascade's kept draws with no culling; its fragment stage loads L1 and B1 at
  its texel and discards a fragment whose body equals B1 and whose depth lies
  within `ε` of L1. `ε` is a few units in the last place of the stored depth,
  below, never a world constant, and it never applies between two bodies.

A receiver `P` of body `b` on a triangle whose camera-side normal is turned
from the light reads, at each filter tap `t`:

- **B1(t) ≠ b**: another body is the nearest surface along that texel's ray.
  It shadows `P` if it lies in front of `P` (L1 compared as the lit side
  compares it). Nothing about `P`'s own body enters: not its thickness, its
  entry, its clip or its winding.
- **B1(t) = b**: `P`'s own body is nearest there. If `b` is `Solid`
  ([below](#the-solid-bit)), the surface that can shadow `P` is L2(t): either
  another body's surface in front of `P`, or a second chord of `b` (its exit
  in front of `P`), or `P`'s own exit, which lies on `P`'s plane and reads
  lit. If `b` is not `Solid` (a sheet mesh), L1(t) is either `P`'s own surface
  on its plane, lit, or another sheet of the same mesh in front, a blocker.

A receiver facing the light reads L1 at every tap, as today, and reads B1 only
when its material has wrap (its grazing band matters then; a plain material's
cosine hides it). The percentage-closer filter, the in-cone blocker search and
the 4×4 views average keep their shape; the per-tap comparison changes as the
[grazing section](#the-receiver-plane-and-the-grazing-rule) states.

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
  chord: with B1 = b and `Solid`, L2 at a tap is `P`'s own exit, on `P`'s
  plane, and the plane test reads it lit at any angle. A second chord of the
  same body is L2 in front of `P`'s plane, a blocker. For a sheet mesh, own
  surface in front of `P` is another sheet, a blocker.
- **Coincident surfaces of different bodies** are never merged: `ε` applies
  only to body B1's own surfaces, so a sheet pressed against a box front, a
  box standing on a slab, or a stack of thin cards of different placements
  all keep their surfaces. Only a chord of one body thinner than `ε` along the
  light collapses to one surface, which reads that chord's dark side lit.
- The only **inference** left, `Solid`, governs one thing: whether a body
  shadows its own dark side by its second chord (L2) or by any own surface in
  front (L1). A closed mesh wrongly read as a sheet reads its own entry as a
  blocker and loses its wrap; a sheet mesh wrongly read as `Solid` lets its
  own other sheets through to its dark side. Both errors are confined to one
  body shadowing itself. No cross-body shadow depends on the bit.

### The counterexamples

The review's cases, and the term's answer to each. Depth is measured away
from the light.

| Case | Layers at the tap | Answer |
|---|---|---|
| Portal-clipped `Solid` body, entry clipped, receiver at 10 m, sheet at 8 m | L1 = sheet (B1 = sheet) | B1 ≠ b: shadowed by L1 |
| The same without the sheet | L1 = P (B1 = b), L2 behind | Own surface on the plane: lit |
| 3 mm closed panel at n·l = -0.25, sheet 1 m ahead | L1 = sheet | B1 ≠ b: shadowed; the panel's 12 mm chord never enters |
| Sheet at 0, thick box front at 0.5 mm, box back at 1 m (receiver) | L1 = sheet or box front (a tie); L2 = the other | B1 = sheet: shadowed by L1. B1 = box: L2 = sheet (another body, not merged), in front: shadowed |
| Triangle drawn as ABC and ACB, receiver on its back, sheet 1 m ahead | L1 = sheet | B1 ≠ b: shadowed. The doubled triangle's classification is irrelevant to a cross-body shadow (it is also a sheet by the test below, having zero volume) |
| Thin foil blocker whose entry and exit fall within `ε` | L1 = foil (B1 = foil) | B1 ≠ b: shadowed |
| Arm (same skinned body) over the torso's dark side | L1 = arm entry (B1 = b), L2 = arm exit | L2 in front of the plane: shadowed (the chord rule) |
| Two shells of one authored mesh, the inner one wound inward | L1 = outer entry, L2 = outer exit | Depth and identity only; winding unread. Receiver on the outer shell's back: L2 = P, lit. On the inner shell's back: L1 = outer entry (B1 = b), L2 = outer exit in front: shadowed, a second chord |
| Stack of a body's own cards within `ε` | collapsed | The body's own thin stack reads lit on its dark side; another body's card is never merged |
| Inside a closed authored room (the camera and receiver inside) | L1 = near wall outer, L2 = near wall inner | Receiver on the near wall's inner face: L2 = P, lit (wrap through the wall). On the far wall's inner face (facing the light): L1 in front: shadowed |
| Grazing plate at n·l = -0.005, nothing in front | L1 = own entry (B1 = b), L2 = own exit | The plane test reads the own exit lit; see the grazing rule |

### `ε` and its envelope

The peel's tolerance exists for one purpose: the same triangle of body B1,
rasterized by the L1 pass and again by the peel pass, must be recognised as
the same surface. Both passes use one vertex stage (`sdf-mesh-shadow.vert`),
one viewport and one rasterizer state, so the interpolated depth is the same
value in both passes on either backend; `ε` is a margin against a driver
compiling that vertex stage differently against two fragment stages. It is
`4 ulp` of the stored `D32_FLOAT` depth at L1, compared in the stored
encoding, never in metres.

In metres, at the near half of a cascade's reversed-Z range where depth lies
in [0.5, 1), one ulp is `R · 2^-24` with `R` the cascade's depth range
`2 · radius + casters - 1` (`SdfMeshShadowCascades.Cascade`). With the
default coverage (`SdfShadowCoverage.Default`: 16 m near, 120 m far, 200 m of
casters) the near cascade's `R` is about 231 m and the far cascade's about
439 m, so `ε` is 55 µm and 105 µm. An authored coverage of 20 km gives
4.8 mm. A chord of one body thinner than `ε` along the light reads lit on its
dark side; no other surface, of any body, is affected. The implementing lane
adds a device witness on both backends: one triangle drawn through the L1
pipeline and the peel pipeline leaves the peel target clear at every texel
the triangle covers.

The `R32_UINT` body attachment is exact. A body index is at most the frame's
draw count (`SdfMesh.MaxDraws`, 2^24).

### The `Solid` bit

`SdfMesh.Encloses` becomes `SdfMesh.Solid`: a body whose surfaces along any
ray come in entry and exit pairs, so its dark side is shadowed by its second
chord. `SdfMeshDraw.EnclosesSolid` is deleted: the determinant and clip-plane
tests served the exit layer and mean nothing here.

| Mesh | `Solid` | Why |
|---|---|---|
| Baked (`SdfMeshPathBake`), rigid or skinned, any level, any chunk | true | The contour winds the field's boundary; a skin whose member folds a chord through itself reads that fold's dark side against the wrong layer and over-shadows it, and shadows every other body exactly |
| Authored, closed in the rendered embedding (the test below) | true | |
| Authored, open, non-manifold, or with a zero-volume component | false | A sheet, a soup, a doubled triangle, a mesh with an internal wall |
| Authored with `solid: true` or `solid: false` on its `mesh` | as written | The author's word outranks the test |

The test is geometric, so two spellings of one surface classify alike
("ergonomic is optimal"): weld vertices by position within `10^-5` of the
mesh's extent; split every edge at a welded vertex that lies on its interior
within the same tolerance (a T-junction is the same surface as its conforming
triangulation); require every undirected edge to belong to exactly two
triangles (winding does not enter: a box with one face reversed is the same
closed surface); orient each edge-connected component consistently by
walking its triangles; and require each component's volume to be non-zero.
A mesh passes when every component passes. Winding, triangulation and vertex
duplication are spellings the test ignores; the geometry it reads is the
surface itself.

What still needs the author's word, and why the test cannot supply it:

- A **non-manifold closed mesh** (an edge shared by three or more triangles:
  a box with an internal partition). The partition is a sheet inside a
  solid; "solid" is a choice about which surfaces pair, which the geometry
  does not make. `solid: true` reads the box as a solid and lets the partition
  through to the walls' dark sides.
- A mesh whose **gaps exceed the weld tolerance**: not closed as authored.
  The author closes it or declares it.
- A closed mesh the author wants to **self-shadow with every own surface**
  (`solid: false`), a lantern whose inner wall should darken its outer wall.

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

### The receiver-plane and the grazing rule

The receiver-plane comparison (`sdfMeshShadowGap`: each tap compares against
the receiver's plane extended to the tap's texel centre) is kept, and its
clamp (`lightNormal.z` floored at 0.2) is removed outright, not lowered: the
review showed that a floor of 0.02 moves the self-shadow from n·l = -0.05 to
n·l = -0.005 (visibility 0.57 to 0.88 on an unobstructed plate) rather than
removing it. The gradient `g` (depth per texel along the cascade's axes) is
exact for the triangle the cascade rasterized, with only a `10^-6` floor on
`|n·l|` against division by zero.

An exact plane extrapolated far along a grazing receiver is exact only where
the receiver's body extends; past its silhouette, the stored depth belongs to
something else and the comparison invents a blocker or a hole. Identity
settles where the plane may be trusted, and the rule is:

1. **Footprint.** A tap's offset `δ` (in texels) from the disk pattern has
   its component along `ĝ` clamped to `± S / |g|`, with `S` the resolvable
   depth span in texels (4 at first; the lane measures). The footprint is a
   disk for a receiver facing the light squarely and narrows to a line across
   the gradient as the receiver turns edge-on. No tap is dropped and no
   receiver is skipped.
2. **Reads.** `delta` is the exact offset from `P`'s projection to the tap
   texel's centre. Read L1(t) and B1(t); for a dark-side `Solid` receiver with
   B1(t) = b, read L2(t). Choose the layer `L(t)` as the term states.
3. **Own-surface test** (B1(t) = b only). `plane(t) = P + g · delta`. If
   `|L(t) - plane(t)| ≤ u(t)`, the stored surface is the receiver's own
   surface on its own plane: the tap reads lit and known. `u(t)` is the
   plane's uncertainty: a depth-noise floor of a few ulp plus the curvature
   term `κ · |delta · texel|² / 2`, with `κ` the record's mean curvature. For
   a planar receiver the test passes at any angle, which is the round-3 high.
4. **Blocker comparison** (every other tap). The reference is the plane with
   its extrapolation bounded: `bounded(t) = P + clamp(g · delta, -S, S) ·
   texel`. `gap.x = bounded(t) - L(t) - u(t)`; the cone gap `gap.y` is as
   today. Where `|g · delta| ≤ S`, which the footprint makes the rule for
   every tap except through texel rounding, this is the exact plane.

The toward-light offset (`NormalOffsetTexels`, 1.5 texels), the constant
bias (`ConstantBias`, 2 cm) and the isotropic slope allowance
(`SdfMeshShadowSlopeBias`, 2.5 texels per texel of lateral distance) existed
to cover a plane that was not exact and taps that could not tell the
receiver's own surface from a blocker; `u(t)` and the own-surface test take
their place. The review's 6 cm slab exit over a panel at n·l = -0.25, lost
today because 4.3 cm of offset and bias plus 2.5 texels of allowance exceed
the 6 cm gap at every tap, reads `gap.x ≈ 0.06 - u` and is shadowed.

The floors, stated as what the cascade cannot resolve:

- **Near contact.** A surface of another body within `u(t) + soft` of the
  receiver's plane along the light reads by the soft ramp
  (`SdfMeshShadowSoftTexels`, one texel: 1.6 cm at the near cascade at Full,
  11.7 cm at the far cascade). This is the shadow map's resolution.
- **Grazing.** At a tap where `|g · delta| > S` after rounding, which happens
  only when `|g| > 2S` (within 7 degrees of edge-on at `S = 4`), the reference
  is `P ± S · texel`, so another body within `S` texels of depth of `P` at
  that tap reads by the ramp rather than exactly: 6.25 cm at the near cascade
  at Full, 47 cm at the far. A body farther in front shadows exactly, a body
  behind reads open exactly.
- **Own chord thinner than `ε`.** Reads lit on its dark side.

None of these is a leak of a blocker at a distance; each is a band of
`S` texels or less of depth around the receiver, and the lane records the
numbers it measures beside these.

## What each caster class gets

| Caster class | As a blocker | As a wrapped receiver |
|---|---|---|
| Baked SDF creation, static | Its surfaces carry its body; the nearest of them shadows any other body behind it | `Solid`; its exit is L2 on its plane; wrap survives in the open; any other body in front shadows it |
| Baked, skinned or deforming | Every drawn surface counts, however the members move | `Solid`; a folded chord over-shadows locally, never leaks |
| Baked, chunked or at a coarser level | Chunks share the body; collapsed folds are surfaces | As the static case; a chord across a chunk seam is one body |
| Authored, closed by the test | Entry and exit count, winding unread | `Solid` |
| Authored, multi-shell | Each shell's surfaces count; no orientation exists to be wrong | `Solid` if every shell has volume; camera sees every wall |
| Authored, T-junctioned closed | Counts whole | `Solid`: the test splits the junction |
| Authored, open card, two-sided card, foliage card | One surface, drawn once | A sheet: shadowed by anything of another body in front; its own other cards shadow it too; lit alone |
| Portal-clipped body | Its remaining surfaces carry its body | A sheet ahead is B1 ≠ b, shadowed; alone, its own surface or exit reads lit |
| Mirrored draw | Its surfaces where drawn | Its mesh's `Solid`; no determinant test |
| Baked thin foliage (the standard's leaves and blades, closed slabs) | Entry and exit; thinner than `ε` along the light, one surface | `Solid`; another leaf of another placement is another body and shadows; a leaf of the same placement is the chord rule |
| Glass | Skipped by the cascades, as today | — |

## What the review rounds' findings become

Round 1: the curvature estimate is gone; the wrap-in-identity medium is fixed
(`SdfWorldTables.Cadence.cs`, `m_wrapSignature`); the cel-style mediums stay
in the [Lighting and GI packet](open-items.md#lighting-and-gi).

Round 2: the normal offset's inverse-cosine leak is closed by the identity
reads and the bounded plane, which replace both offsets; an open or clipped
caster is a body whose surfaces count whatever faces survive; a two-sided
authored mesh is drawn once in the cascades and its winding is unread.

Round 3: the grazing clamp is removed and the own-surface test replaces it;
no orientation is computed anywhere, so one signed volume orients nothing;
a T-junction is split by the test; a skin's inverted winding is unread.

The first draft's review: each counterexample is in the
[table above](#the-counterexamples); the slope clamp is gone rather than
smaller; `ε` has its envelope; GI reads no camera cascade
([composition](#composition)); V5's update is specified; the cost section is a
measurement plan; each citation states what it supports.

From the packet: the portal straddler's wrapped side shadowed by its own lit
side reads lit; the grazing slab leak is the stated floor, narrowed to one
texel of depth.

## Cost: a measurement plan

What is known from the illum1-fix measurement at the stage on Direct3D 12
(`world.counters gpu pass sdf.world$shadow-near`): 460 draws and 2.62 M
vertices over the cascade's two render passes, both of which draw the same
kept set through `RecordMeshDraw` with the same vertex counts, so each pass
submits about 1.31 M vertices; front-face culling discards after the vertex
stage and saves fragments, not vertices. Today's second pass therefore costs
the depth pass's vertex work and about half its fragments. The term's second
pass costs the same vertex work and the depth pass's fragments, each running
two loads and a compare. Up to three cascades can refresh in one frame (near,
far when its rate allows, the second light's). No timing of the exit layer on
a quiet machine exists, so the plan is to measure, not to estimate:

- **Devices and tiers.** The RTX 2060 at Full (2048 texels), the RTX 4070 at
  Full, the Steam Deck at its tier (Half, 1024 texels, under
  `WorldFramePresenter.Quality.ShadowResolutionOf`). Both backends on the
  desktop cards.
- **Stops.** `debug.go stage` (the step 1 witness), the meadow (the most
  wrapped receivers and the most casters), `debug.go portals` (nested views
  each with cascades), `valley-rim` (the far cascade's draw count).
- **Readings.** `world.gpu-timing` for `shadow-near`, `shadow-far`,
  `shadow-second` and `surface`; `world.counters gpu pass sdf.world$shadow-*`
  for draws, vertices and fragments; owned bytes for the attachments. Each
  stop before and after, with `world.cadence off` and `world.rates off`, read
  twice and the second kept.
- **Budgets.** On the RTX 2060 at Full: the three cascades together within
  1.5 ms and the surface pass within 0.3 ms of today's reading, inside the
  16.7 ms frame. On the Steam Deck at Half: the cascades together within
  2.5 ms inside the 22.2 ms frame. A budget missed is fixed at its cause,
  never by restoring the exit layer (which the first review showed leaks and
  so is not a baseline): the candidate causes and fixes are below.
- **Two peel implementations, one output.** (a) A depth-tested raster with a
  discard-only fragment stage and no counter. Its fragment stage has a side
  effect of none, so the driver may test depth early with the write deferred;
  MJP measured on Direct3D 12 that a `discard` in the instruction stream with
  depth writes enabled still ran 582 k pixel invocations against 441 k without
  it on an RX 7900 XT unless draws were serialised
  (<https://therealmjp.github.io/posts/to-earlyz-or-not-to-earlyz/>), so the
  overhead is bounded by one depth pass's overdraw and must be measured.
  (b) A raster with no depth attachment whose fragment stage computes its
  depth and `InterlockedMin`s it into an `R32_UINT` L2 image after the identity
  test, as the glass sums accumulate (`frame/sdf-glass.hlsli`); every
  rasterized fragment runs, with two loads and one atomic, and there is no
  early test to lose. Both produce the same L2; the lane keeps whichever is
  cheaper on the RTX 2060 and the Steam Deck, which may differ.
- **Counters.** The L1 pass keeps `[earlydepthstencil]` and
  `puckCountFragmentWork`, as today. The discard peel counts no fragments:
  a UAV write in the stage disables early rejection on Direct3D 12 (the HLSL
  early-pixel-culling proposal states this as the reason for its opt-in,
  <https://microsoft.github.io/hlsl-specs/proposals/0053-allow-early-pixel-culling/>),
  and a count of invocations the depth test later rejects would mean nothing.
  The atomic peel counts every fragment it runs, which is its real work.
  `gpu.draws` and `gpu.vertices` count both as any draw.
- **Storage.** Per cascade at Full: L1 `D32` 16 MiB, B1 `R32_UINT` 16 MiB,
  L2 `D32` or `R32_UINT` 16 MiB: 48 MiB, against 32 MiB today; three cascades
  144 MiB against 96 MiB per full-resolution view, before any V5 pyramid or
  cache. At Half, 36 MiB for three. If the body attachment is the cost that
  misses a budget, it becomes `R16_UINT` with bodies numbered per cascade over
  the kept set, which the valley-rim's 7,170 draws fit with room.
- **The surface pass.** A wrapped receiver reads B1 at every tap (28 per
  cascade) and L2 at the taps where B1 = b. Plain materials read no body. The
  meadow is the worst case; the budget above covers it.
- **Confining the peel.** If the peel misses its budget, the first fix is
  Puck-native: only texels under wrapped dark-side receivers need L2, the
  surface pass knows which pixels those were last frame, and their light-space
  footprint bounds the peel's scissor or stencil. The second is the id width
  above.
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
L2), and no prior frame's L2 pyramid may cull the peel either: the mesh
pass's phase two exists because the depth test reports what became visible,
and the peel's target reports nothing about second surfaces. The receiver
mask and the box cull apply to both passes alike. Static cascade caching, if
measured worthwhile, caches L1, B1 and L2 together.

### GI

The irradiance volume reads no camera cascade, today or under this term.
Today `SdfMeshIrradiance.Shade` applies the material's wrap to a hit's
Lambert term and casts the sun's shadow ray from the hit lifted along its
normal (`scene.Unblocked`), so a hit turned from the sun is blocked by its own
body and its wrap contributes no bounce: conservative, no leak. Under the
term, the ray is kept and the rule is applied to its ordered hits through the
static scene (`SdfMeshIrradianceScene`, which holds every static draw at full
reach, independent of the camera's cascades, receiver mask and box cull):
with the hits from the surface toward the sun `h1 … hk`, the surface is
shadowed when `hk` belongs to another body, or its body is not `Solid`, or
after dropping `hk` and the run of its body's hits within `ε` of it any hit
remains. The scene's hit result gains the hit's body and `Solid`
(`SdfMeshIrradianceScene.Intersect` returns distance, normal and material
today); the cached hit buffer of illumination's step 1 needs neither, because
the sun moves and the relight's sun visibility is a shadow ray per relit hit
through the trace-hits interface (decision 3), whose hits carry their
instance. Should the relight ever read a light-space depth instead of rays,
that map is fitted to the volume's windows from every static draw, carries
L1, B1 and L2 of its own, and is the terrain pass's static sun cascade, never
a camera cascade; that choice is made when step 2's relight lands. Dynamic
bodies are not in the static scene and are never probe hits.

### The rest

- **Cel shading.** `band(cosine) × visibility` is unchanged; this design
  supplies the visibility.
- **The far cascade's hold** (`SdfUpdateRates.ShadowFar`): L1, B1 and L2 are
  drawn and held together under the held camera.
- **Portal and mirror views.** Each nested view fits its own cascades and
  runs the same passes.

## Rejected

- **Depth order alone** (this plan's first draft): the nearest surface and a
  peel, read by a closed bit. The review's six counterexamples above; the
  root is that two depth layers cannot say which body a surface belongs to,
  and the bit's inference is not one-directional (a doubled triangle passes
  edge pairing).
- **The exit layer** (rounds 2 and 3). Sound only when every drawn face's
  winding says which side the light is on; a clipped body whose exit is
  clipped away, or a light-facing sheet treated as enclosing, leaks. Not a
  fallback: a term that leaks is not a baseline.
- **A body rule without the chord rule** (own matter never shadows own dark
  side). One layer and one id would suffice, and the arm's shadow on the
  torso would end at the terminator as a seam of `wrap / (1 + wrap)`.
- **Thickness baked from the field** (per vertex or per texel). The chord
  through a body depends on the light's direction; a scalar follows it only
  at normal incidence; a directional basis multiplies vertex words; neither
  exists for an authored mesh or follows a skin's members. Round 1 was its
  curvature-shaped cousin, and the industry's baked-thickness translucency
  (Barré-Brisebois and Bouchard) calls itself an approximation.
- **Field visibility for SDF creations, raster for authored meshes.** A
  per-pixel walk of the receiver's field toward the light is a second
  `mapCore` call site in a pixel kernel, which the renderer forbids, and
  leaves the authored path on a different mechanism.
- **Shadow-map thickness as transmittance** (`exp(-(P - L1) / σ)`, Jimenez et
  al.; Frostbite). One layer, no bit, physically motivated, and a different
  look: a thick body's dark side goes dark, which step 1 ruled out. Noted
  under the owner's calls as a possible separate material term.
- **Ray-traced shadows.** Exact and identity-aware, but the Steam Deck has no
  hardware ray tracing in the budget; upper hardware enhances, never carries
  the baseline.
- **Stencil shadow volumes.** Orientation-dependent and fill-bound.

## Implementation steps

Each is one lane with a mechanical check. The first two are serial; the
third and fourth run beside the second.

1. **`transmit-identity`: the body word and the two layers.** The tables
   assign each body a frame index and write `SDF_MESH_BODY_WORD` (chunks share
   their group's, as `SdfMeshPathBake` records chunk owners); the L1 pass gains
   the `R32_UINT` body attachment; `ShadowExitPipeline` becomes the peel
   (both implementations behind one pipeline description, selected by a
   lever for the measurement); `RecordExitDraws` becomes one pass over the
   kept draws after L1 is final; `sdfMeshShadowDepth` reads L1, B1 and L2;
   `sdfMeshShadow` applies the term's layer choice. `SdfMeshDraw.EnclosesSolid`
   is deleted and `SdfMesh.Encloses` renamed `Solid`. A palette with no
   positive wrap records neither the attachment nor the peel.
   Check: the `ε` device witness on both backends; settled captures on both
   backends at the plinth stop (the Moth's cheek under the slab dark, in the
   open lit) and at `debug.go portals` with the avatar straddling the glass
   (its dark side keeps wrap, and a sheet placed between it and the sun
   shadows it); `world.counters gpu pass sdf.world$shadow-near` shows one
   peel pass with the depth pass's draws; the measurement plan's readings
   recorded here.
2. **`transmit-plane`: the grazing rule.** The footprint clamp, the exact
   gradient, the own-surface test with `u(t)`, the bounded reference; the
   offsets, constant bias and slope allowance deleted; `S` and `u`'s floor
   set by measurement.
   Check: `world.debug-view shadow` on a flat wrapped plate at n·l = -0.005
   and at -0.05 in the open reads 1 across it on both backends; the 6 cm slab
   exit over the panel at n·l = -0.25 shadows it; the Moth's cheek shows no
   acne along its terminator under `world.toon off`.
3. **`transmit-authored`: authored meshes.** `MeshOf` draws every authored
   mesh two-sided and records the cascade's single-winding count; the closure
   test (weld, T-split, undirected pairing, per-component orientation and
   volume) replaces `ClosedOutward`; `WorldPrototypeMesh.Solid`; `puck
   schema`; the lint report.
   Check: a scratch world beside the debug world's shapes row with (a) two
   shells in one mesh, a 2 m cube wound outward and a 1 m cube wound inward,
   both showing every exterior wall, the inner shell's back shadowed; (b) a
   T-junctioned closed box with wrap keeping its far side's wrap with no
   declaration, and the same box with one face reversed keeping it too;
   (c) a wrapped open card behind an open card of another placement,
   shadowed, and behind a card of its own mesh, shadowed; (d) a doubled
   triangle classified a sheet; (e) a box with an internal partition
   classified a sheet, and `solid: true` making it solid.
4. **`transmit-gi`: the rule on the trace.** `SdfMeshIrradianceScene`'s hit
   result carries body and `Solid`; `Shade`'s sun ray collects ordered hits
   and applies the rule.
   Check: the CPU irradiance law with a wrapped slab under the sun and a
   sheet above it: the slab's dark side bounces nothing; the sheet removed,
   it bounces its wrap.
5. **`transmit-docs`**, folded into whichever lane lands last: the
   [decision](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)
   rewritten for the term, the handbook's
   [shadow passage](../rendering/sdf/handbook/lighting-and-shading.md), the
   rendering skill's soft-shadow bullet, the V5 note in
   [visibility](visibility.md#v5-multi-view-and-shadow-work), and the packet.

## The owner's calls

- **The chord rule is the look chosen here for a body's own overhangs**: the
  Moth's wing shadows her torso's dark side as it shadows her lit side, so the
  shadow is continuous across the terminator. The alternative (own matter
  never shadows own dark side) is cheaper by one layer and shows a seam of
  `wrap / (1 + wrap)` at the terminator under every overhang. Confirm.
- **Wrap stays thickness-independent.** A physically attenuated transmission
  (shadow-map thickness) is a different look and could be a separate authored
  material term later; it is not folded into `wrap`.
- **`solid: true | false` is author-facing vocabulary** on a prototype's
  `mesh`. The test infers it for every manifold closed mesh however it is
  triangulated or wound; the word is for non-manifold meshes and for
  overriding.
- **The floors are accepted**: another body within one texel of depth of a
  receiver reads by the soft ramp, and within `S` texels at a receiver within
  7 degrees of edge-on; a body's own chord thinner than `ε` (55 µm at the
  default near cascade) reads lit. These are the shadow map's resolution, not
  leaks at a distance.

## References

What each supports, and nothing more:

- Wang and Molnar, "Second-Depth Shadow Mapping", UNC-CS TR94-019, 1994,
  <https://www.cs.unc.edu/techreports/94-019.pdf>: the mechanism of a
  second-depth layer per light texel, and the observation that a single
  non-solid surface has coincident first and second depths. Its correctness
  argument rests on the second surface being dark, which Puck's wrap denies,
  so it supports the mechanism and not this term's correctness.
- Everitt, "Interactive Order-Independent Transparency", NVIDIA, 2001,
  <https://developer.download.nvidia.com/assets/gamedev/docs/OrderIndependentTransparency.pdf>:
  depth peeling as a raster pass that discards against a previous layer.
- Bavoil and Myers, "Order Independent Transparency with Dual Depth
  Peeling", NVIDIA, 2008,
  <https://developer.download.nvidia.com/SDK/10/opengl/src/dual_depth_peeling/doc/DualDepthPeeling.pdf>:
  that a min/max blend yields the nearest and farthest layers, not the
  second-nearest, so it does not replace the peel.
- Isidoro, "Shadow Mapping: GPU-based Tips and Techniques", GDC 2006,
  <https://gdcvault.com/play/1013442/Shadow-Mapping-Tricks-and>: the
  receiver-plane depth bias, comparing each tap against the receiver's plane
  at that tap, which `sdfMeshShadowGap` implements with the face normal in
  place of screen derivatives.
- MJP, "To Early-Z, or Not To Early-Z",
  <https://therealmjp.github.io/posts/to-earlyz-or-not-to-earlyz/>: the
  measured invocation counts with and without `discard` under depth writes on
  Direct3D 12, and that UAV writes force the late test by default.
- Microsoft, HLSL proposal 0053 "Allow early pixel culling",
  <https://microsoft.github.io/hlsl-specs/proposals/0053-allow-early-pixel-culling/>:
  that UAV access in a pixel shader disables early depth and stencil
  optimisation today and that the opt-in is under consideration, not a
  baseline capability.
- Jacobson, Kavan and Sorkine-Hornung, "Robust Inside-Outside Segmentation
  using Generalized Winding Numbers", SIGGRAPH 2013,
  <https://users.cs.utah.edu/~ladislav/jacobson13robust/jacobson13robust.pdf>:
  the state of the art for inside-outside classification of imperfect
  meshes, a problem this term does not need solved. Takayama, Jacobson, Kavan
  and Sorkine-Hornung, 2014, <https://arxiv.org/pdf/1406.5431>, reports that
  their orientation method fails on most handmade meshes; it is cited for
  that method's limits, not as a theorem about orientation.
- Barré-Brisebois and Bouchard, "Approximating Translucency for a Fast,
  Cheap and Convincing Subsurface Scattering Look", GDC 2011,
  <https://frostbite.com/frostbite/news/approximating-translucency-for-a-fast-cheap-and-convincing-subsurface-scattering-look>:
  baked per-vertex thickness as an approximation of transmission, rejected
  above for the reasons its authors give.
- Jimenez, Whelan, Sundstedt and Gutierrez, "Real-Time Realistic Skin
  Translucency", IEEE CG&A 30(4), 2010,
  <https://graphics.unizar.es/papers/Real-Time-Realistic-Skin-Translucency.pdf>:
  shadow-map thickness as the transmittance distance, the rejected look.
- Haar and Aaltonen, "GPU-Driven Rendering Pipelines", SIGGRAPH 2015: the
  light-space caster cull V5 adopts, cited in [visibility](visibility.md).

Each citation was checked against its hosting page; the MJP post and the
HLSL proposal were read for the claims above.

---

[Plans](README.md) · [Illumination](illumination.md) · [Visibility](visibility.md) · [Decisions](../decisions/rendering.md)
