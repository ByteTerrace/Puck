# Rendering

The frame rasterizes baked SDF meshes with shadow cascades and an irradiance
volume traced on the GPU. This plan holds the remaining sky vocabulary, wallpaper
bounds, rendering follow-ups and hardware observations. The debug world
(`worlds/debug`) proves the work on both backends; features from experimental
worlds are ported into it rather than reviving those worlds.

The [rendering decisions](../decisions/rendering.md) own the design, the
[shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
owns pipeline authoring, and the [frame handbook](../rendering/sdf/handbook/frame-rendering.md)
and [rendering skill](../../.claude/skills/rendering/SKILL.md) own the implemented
contracts. Delivered package history belongs in git.

## What is open

Each package below still owes something; a package with only a hardware
observation left is listed under [deferred checks](#deferred-to-the-end). The
[shared queue](open-items.md#next) ranks work across programmes.

| Package | Open remainder |
|---|---|
| P1a, durable functional fixtures | [No-driver boot](#deferred-to-the-end) |
| P1b, foundation qualification | [Reference devices and driver removal](#p1b--foundation-qualification) |
| P10, bound rows and tiers | [Hardware qualification at high](#deferred-to-the-end) |
| P12, image sources | [Real camera](#deferred-to-the-end) |
| P13, hit-to-source mapping and input | [Windows passthrough click](#p13--hit-to-source-mapping-and-input-destinations) |
| P14, SDF pass package | [Bake quality, glass, submission and routed worlds](#p14--the-sdf-engine-as-a-pass-package) |
| P15, temporal reconstruction | [The march-step signal and the Steam Deck observation](#p15--temporal-reconstruction) |
| P16, display output | [HDR display](#deferred-to-the-end) |
| P18, sky and atmosphere | [Celestial bodies and infinity-view admission](#p18--sky-and-atmosphere) |
| P19, wallpaper-fold bounds | [CPU certification and bake lattice](#p19--neighbour-content-bounds-for-wallpaper-folds) |
| P20, visibility and residency | [Observations](#p20--visibility-areas-and-residency) |

## The forcing artifact

Every change proves itself in the debug world: its device row, gallery, nested
screens and portals, sky and valley. Captures and live counters show what a
change does; `host.frames` and `world.gpu-timing` on a quiet machine show its
cost. Canaries under `experimental/tests/Puck.World.Canaries` are quarantined.

[The frame bar](../decisions/rendering.md#the-pipeline-foundation) is the
worst frame at the highest quality on every bar device, read by
[one recipe](streaming-and-memory.md#how-the-bars-are-read) with counted work
beside it. The [engine job system](open-items.md#engine-job-system)
owns scheduling, parallel tick execution and view recording; the
[bake work](open-items.md#bake-proportional-to-change) owns bake speed and identity.

## Packages

### P1b — Foundation qualification

The remaining reference-device and driver-removal observations are listed
under [deferred checks](#deferred-to-the-end). Qualify the debug world at
`high` and retain the actual memory and timing readings. No recorded work-count
ceiling substitutes for a device observation.

### Global illumination

The [illumination plan](illumination.md) holds the lighting decisions and their
build order. The [Lighting and GI packet](open-items.md#lighting-and-gi)
owns the remaining coverage, visibility, exposure and bounce findings. The
[engine job system](open-items.md#engine-job-system) owns its execution budget,
and the streaming plan's
[decision 9](streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand)
owns what the lighting demands and keeps. No second scheduler is built for GI.

### P13 — Hit-to-source mapping and input destinations

The shared picker and mapping are implemented. The one remaining P13b-4
observation is a real Windows editor click and focus return, listed under
[deferred checks](#deferred-to-the-end). The
[editor plan](editor.md#e2--selection-picking-and-highlight) consumes the same
picker.

### P14 — The SDF engine as a pass package

The frame-graph package is implemented. Its editor levers belong to the
[editor plan](editor.md). Crossings, Vulkan robustness and GPU
qualification belong to [cross-backend work](open-items.md#vulkan-regressions-and-gpu-robustness);
first-use JIT belongs to [boot work](open-items.md#edit-to-running-hot-reload-and-boot).
The remaining rendering-specific details follow.

#### Bake quality

The [collision-versus-mesh packet](open-items.md#collision-versus-drawn-mesh)
owns sub-cell features and contact against the drawn surface; every shape's bake detail is one rule
(`WorldShapeDetail.Of`, [mesh bake](../../.claude/skills/rendering/references/mesh-bake.md#groups-poses-and-materials)).
The remaining bake investigations are below, with an engine gap first. The
[rendering plan's list](rendering-research.md#engine-gaps-filled-first) numbers
it 11: a primitive under a non-uniform scale answers a bound on distance, not a
distance, so any stretched cutter (the lake's bowl among them) frays the
surface it cuts. Its design is decided, and it goes ahead of every item after
it (the owner's ruling, [AGENTS.md](../../AGENTS.md#pucks-purpose)).

- Give every primitive under a non-uniform scale a true distance, keeping the
  authored surface
  ([the decision](../decisions/engine-design.md#a-scaled-primitive-keeps-its-authored-surface-at-a-true-distance)).
  What reads a bound today, which `puck creation shape` reports as a gain: a
  capsule, cone or torus scaled apart in X and Z, a round cone scaled apart at
  all, and an ellipsoid or sphere scaled apart, which is the exponent-2
  superellipsoid gauge. A `Path` profile and a sweep certify no gain, and are
  not part of this item. The solve, its proof, its exhibits and its build
  order are [designed below](#a-true-distance-under-a-non-uniform-scale); its
  first two steps need no new solver and unblock the lake's bowl.
- Finish an in-flight group that a later plan drops and retain it in the library
  and store. Read back a boot's second, different `start` plan so churn cannot
  silently rebake discarded work on every boot.
- Inspect the Moth's hood opening, shin knife edges, left pauldron underside,
  lower eyelid, scalp, pod bands and greave rim. Touching mesh groups expose
  seams at back-pod plates and nozzle discs; contour a touching pair together
  where that expresses the authored shape. Material cuts still leave a hairline
  at concave creases, and the arch bands' faceted outer rims are geometry.
- Inspect the gallery's anisotropic cone, ellipse prism, path fill and shear,
  revolved and flared feet, polar-fold corners and cell ridges. Resolve the
  refined contour's two-patch ambiguity and fine-column crease slivers,
  including `shape-jitter-tumble`, `shape-polygon-3` and `shape-blend-xor`.
  The Direct3D 12-only blisters on `shape-wallpaper-pmm` need a GPU/CPU tile-height
  slice; agreement of the fields still leaves the bake sampling to inspect.
- Give long thin static-union members appropriate spacing and sample capacity.
  The studio grid and hall shell expose the mismatch between a long member's
  bound and its thin section. The audit's uncovered-surface lattice must cover
  the planned group or each member, including a member dropped whole, rather
  than only the surviving mesh's box.
- Inspect sharp-edge rounding from corner-gradient interpolation: measure
  crossing gradients and surface positions, and compare the contour with its
  field. Keep the actual bake-cell error visible.
- Extend the live mesh audit to annotate clusters through the existing overlay
  writer. Key the segments no instance owns, which the offline rest scene
  cannot identify while the World emits adjacent fields and documents.
- Review the warps II row, motion exhibits and hall-light switches on the GPU.
  A switch's reach check can gain a body-yaw operand beside `bodyPosition`;
  switching a level's lights must not rebake its geometry. Vesica and Star
  remain builder-only shape types without creation primitives.

A distributed mesh cache waits until a presenting client downloads packages.
At that point a GPU runner bakes through a `puck mesh bake` verb, retains only
used entries, and supplies an optional read-only package cache ahead of the
user store. Deployment proceeds without it on a miss; the client derives
missing meshes. WARP and the fixed-point CPU audit are not substitutes for
that GPU product. The [derived-product decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)
owns identity and cache semantics; the bake packet owns the derivation closure
and shared store before this distribution work.

##### A true distance under a non-uniform scale

This is the design for gap 11: what the gap is in the code, the two exhibits
and the reading each fails, the solve for each primitive, why its answer is
the distance, where it may stop, and the build order with a check for each
step. The last part lists the claims a review should attack.

**What the gap is.** `SdfSolidGeometry.AppendScaledPrimitive` spells a scale
into a primitive's own dimensions wherever a closed form exists. A capsule,
cone or torus whose X and Z scales differ, and a round cone under any uneven
scale, have none and fall to its last arm, a `Scale` instruction over the unit
shape. That instruction evaluates `f(p / s) * min(s)`
(`SdfProgramBuilder.Scale`): zero on the authored surface, and between
`min(s) / max(s)` and one times the distance everywhere else
(`SdfProgram.InstanceDistanceGain`). The fixed-point evaluator refuses it for
contact.

A stretched sphere or ellipsoid is in the same position. It lowers to the
exponent-2 superellipsoid gauge, `(|p / r| - 1) * min(r)`, which is a bound
with the same gain: the gallery's `shape-ellipsoid-xyz` reads
`distance=bound gain=0.366`. The lake's bowl is one (`terrainBasin`, radii 46,
17 and 46 m, so a gain of 0.37). The ellipsoid is therefore the first solve
here, and the capsule and the round cone are built from it.

**The two effects, measured.** Two exhibits end the gallery's stretched row.
Each presses a capsule, a cone and a torus, scaled apart in X and Z, half way
into a slab's face. `puck creation shape` reads the three as bounds with gains
0.313, 0.25 and 0.471.

| Effect | Exhibit | Reading that fails | The same reading with exact cutters |
|---|---|---|---|
| The distances a smooth blend reads away from the surface | `shape-stretch-smooth`, cut at a 0.04 m radius | The fillet bends the slab's face out to 96 to 128 mm from the capsule's rim, 104 to 160 mm from the cone's and 40 to 85 mm from the torus's, where the author wrote 40 mm all round. `puck mesh audit --cpu` reads `field slope deficit` over the threshold on 4177 of 76778 triangles, with 10 folds and 3 flipped triangles | Deficit over the threshold on none, no fold, no flip |
| Defects in a hard cut's contour | `shape-stretch-cut` | `puck mesh audit --cpu` reads 6 folds, 1 flipped triangle and 1263 of 45817 faces flagged for turning from the field's gradient, its worst clusters all along the three rims, and the deficit over the threshold on 8358 triangles | No fold, no flip, 300 of 19476 faces flagged |

The fillet's reach is the true distance from the rim at which the cutter's
field reads the blend radius, computed from the kernel's formulas and a
brute-force closest point in double precision. The control column is the same
exhibit with each cutter's X and Z made equal, so every cutter takes an exact
lowering: its rims are other curves, so it shows what an exact cutter leaves,
not what these cutters will read. In both, 99 in 100 of the hard cut's
vertices lie within half a millimetre of the field's surface, which is why
the two effects are apart: a bound keeps a hard cut's surface and frays only
its rim, and moves a smooth cut's surface itself.

**What exists.** One closest-point solve is already in both interpreters: the
ellipse's projection, a bisection of one bracketed root
(`SdfFieldEvaluator.EllipseRootSteps`, `sdfEllipse2D` in
`field/sdf-ellipse.hlsli`), held to a double-precision reference by
`EllipseDistanceLawTests`. It is what makes an elliptic cylinder, a torus
stretched in Y alone and a revolved ellipse exact. Its interval rule adds a
fixed slack that is asserted in a comment and not derived. The sweep searches
its curve by samples and golden-section rounds with no bound on what it
missed. The reach certificates behind the cull tables (`SdfFieldEvaluator`)
admit a `Scale` only when it is uniform.
The bake searches each crossing along its lattice edge and reads a value as a
distance only over the gradient's length (`SdfSampledLattice.PassesBetween`),
which is why a hard cut survives a bound as well as it does. Nothing projects
onto a triaxial ellipsoid, a cone or a torus.

**Options weighed.**

| Option | What it gives | Why it is or is not taken |
|---|---|---|
| Reduce each primitive to bracketed roots of one variable | The distance on both sides, with an interval from the bracket | Taken. The ellipsoid's reduction is published; the cone's and the torus's are derived below and checked against brute force |
| Newton on the surface's two parameters, with a bound afterwards | A nearby surface point quickly | The point is only an upper bound. Outside a convex shape a lower bound exists (claim 1), inside none does, so it cannot answer a cutter's inside |
| A union of exact pieces (an elliptic cylinder and two ellipsoids for the capsule) | The distance outside | Inside a union the field is the depth in one piece, which is a bound again, and a cutter is read inside |
| Divide the bound by its gradient's length | The distance to first order at the surface | It has no bound away from the surface, where a blend reads it. The bake already does this where it helps |
| Resample the shape into a sampled region | A distance as fine as its cell | Memory for each stretched shape, and exact nowhere |

Refusing such scales or changing the surface is ruled out by
[the decision](../decisions/engine-design.md#a-scaled-primitive-keeps-its-authored-surface-at-a-true-distance).

**The solves.** Every shape here is symmetric in X and in Z, so a point is
read by its absolute X and Z, and by its absolute Y where the shape is
symmetric in Y. `s = (sx, sy, sz)` is the scale and `p` the point in the
shape's own frame.

*The quadric root.* For semi-axes `e` and a point with no negative
coordinate, `F(t) = sum((e_i * p_i / (t + e_i^2))^2) - 1` falls strictly
for `t` above `-min(e_i)^2`, where it has one root. The foot is
`q_i = e_i^2 * p_i / (t + e_i^2)`, the root is positive outside and negative
inside, and with `m` the least axis it lies in
`[-e_m^2 + e_m * p_m, -e_m^2 + |e * p|]` (zero in place of the upper end
inside). With two terms this is the ellipse's root, and one routine serves
both.

*A zero coordinate.* When `p_i` is zero its term vanishes and its pole,
`t = -e_i^2`, still ends the bracket. If the other terms already sum below one
at that pole, the root is the pole and `q_i` comes from the surface's own
equation: the two feet off the plane, which is the branch `sdfEllipse2D` takes
on its major axis. Every solve below takes this branch at its poles.

*The ellipsoid.* The quadric root with three terms. Two equal radii need no
new solve: the shape is the ellipse revolved at offset zero.

*The capsule.* The scaled capsule is the segment from the origin to
`(0, h, 0)`, `h = sy / 2`, swept by the ellipsoid of radii `s`. Its surface
is the lower half of that ellipsoid, an elliptic cylinder between the planes
`y = 0` and `y = h`, and the upper half of the ellipsoid about `(0, h, 0)`.

- `p.y <= 0`: the ellipsoid about the origin, on both sides. `p.y >= h`: the
  ellipsoid about `(0, h, 0)`.
- Between the planes and outside: the ellipse's distance of `(p.x, p.z)` in
  the ellipse `(sx, sz)`.
- Between the planes and inside: the depth is the least of the ellipse's
  depth and, only when `sy` is under both `sx` and `sz`, the far foot on each
  cap. The far foot is the nearest point of a cap from a point on the other
  side of its equator. With `y'` the height over that cap's centre, `F` over
  `(x, y', z)` is convex between the poles `-min(sx, sz)^2` and `-sy^2`, has
  two roots there or none, and the far foot is the larger root. The search
  halves on the sign of `F'` until it meets a point where `F` is negative,
  then halves on `F` between that point and `-sy^2`.

*The cone.* The scaled cone has its apex at `(0, sy, 0)` and its base, the
ellipse `(sx, sz)`, in the plane `y = -sy`. With `Y = p.y - sy` and
`b = 1 / (4 * sy^2)`, its side is the nappe
`x^2 / sx^2 + z^2 / sz^2 = b * Y^2`, `Y <= 0`. A foot on the nappe is
`(sx^2 * p.x / (sx^2 + k), Y / (1 - k * b), sz^2 * p.z / (sz^2 + k))`, where
`k` is a root of
`G(k) = (sx * p.x / (sx^2 + k))^2 + (sz * p.z / (sz^2 + k))^2 - b * Y^2 / (1 - k * b)^2`.

- `Y < 0`: `G` falls strictly from the pole `-min(sx, sz)^2` to the pole
  `1 / b`, so it has one root there, negative inside the nappe.
- `Y >= 0`: with `u = 1 / k`,
  `H(u) = (sx * p.x / (1 + u * sx^2))^2 + (sz * p.z / (1 + u * sz^2))^2 - b * Y^2 / (u - b)^2`
  falls strictly over `(0, b)`. If
  `H(0) = sx^2 * p.x^2 + sz^2 * p.z^2 - Y^2 / b` is not above zero the
  nearest point of the nappe is the apex; otherwise `H` has one root.
- Inside the cone the depth is the lesser of the height over the base,
  `Y + 2 * sy`, and the distance to the nappe's foot.
- Outside, a foot at or above the base is the answer. Otherwise the answer
  is the base: `-2 * sy - Y` straight down when the point is under the base
  and `(p.x, p.z)` lies in its ellipse, and else the rim, the root of the
  squares of `Y + 2 * sy` and the ellipse's distance of `(p.x, p.z)`.

*The torus.* The unit torus has a major radius of one and a tube of ratio
`r = 0.4`. Its points are
`(sx * (1 + r * cos a) * cos f, sy * r * sin a, sz * (1 + r * cos a) * sin f)`
for a tube angle `a` (zero on the outer equator, a quarter turn on top) and an
azimuth `f`. For a point with `p.y > 0` let `m(a) = sy * (p.y / sin a - sy * r)`,
`A(a) = sx * (1 + r * cos a) + m * cos a / sx`, and `B(a)` the same with `sz`.
A foot at tube angle `a` has `cos f = p.x / A` and `sin f = p.z / B`, so the
feet are the roots of `G(a) = (p.x / A)^2 + (p.z / B)^2 - 1`. `m` is the
foot's signed reach along its normal: zero or more for a point outside, below
zero inside.

- Outside there is one root. `G` falls strictly in `cos a` wherever `m` is
  not negative, which is `sin a <= p.y / (sy * r)`. That is every angle when
  `p.y >= sy * r`. Otherwise it is an arc at each equator, and the root is
  in the outer arc when `(p.x / sx)^2 + (p.z / sz)^2 >= 1` and in the inner
  one when not.
- Inside, `G` is below zero at the outer end of the arc between and above
  zero at the inner end. When `sy` is no more than `sx` and `sz`, `A` and
  `B` rise in `cos a` over the whole arc and there is one root. When it is
  more, `A` falls where `sin a` is above the cube root of
  `sy * p.y / (r * (sy^2 - sx^2))` and rises elsewhere, and `B` likewise:
  the arc is cut there into pieces on which each is monotone. Over a piece
  the end values of `A` and `B` bound both squares, so a piece whose bounds
  keep `G` off zero holds no foot; the others are halved until they are
  narrower than the tolerance. The depth is the least distance over the
  pieces left.
- `p.y = 0`: `m` is `-sy^2 * r` at every angle, and the equator feet are
  the ellipse's distances in the plane, to `(1 + r) * (sx, sz)` and to
  `(1 - r) * (sx, sz)`.

The root is searched over the tube angle, never over its cosine: near an
equator the cosine cannot resolve the foot.

*The round cone.* Its surface is the ellipsoid of radii `s` below the plane
`y = sy / 2`, the ellipsoid of radii `s / 2` about `(0, sy, 0)` above
`y = 5 * sy / 4`, and between them a nappe with its apex at `(0, 2 * sy, 0)`
and `b = 1 / (3 * sy^2)`. Outside, the nappe's foot is the answer when it lies
between the planes. Otherwise the answer is the nearest point of whichever
ellipsoid has it on its own cap, which claim 1 confirms for the foot found.
Its inside is not designed here: step 7 owns it.

**Why the answer is the distance.** Each claim is used where the solves above
cite a region or skip a candidate.

1. *A convex shape, outside.* For a convex solid `K`, a point `p` outside it,
   any point `q` of its surface and the outward unit normal `n` there,
   `n . (p - q) <= distance <= |p - q|`. `K` lies behind the plane through
   `q`, so the plane is no farther than `K`, and `q` is a point of `K`. When
   `p - q` is along `n` the two ends meet, so a foot is the nearest point.
   The capsule, the cone, the round cone and the ellipsoid are convex. So is
   the torus's hull, and outside it with `(p.x / sx)^2 + (p.z / sz)^2 >= 1`
   the nearest point of the hull is on the torus's outer half.
2. *An intersection, inside.* A ball about `p` lies in an intersection of
   sets when it lies in each, so the depth in the intersection is the least
   of the depths. The cone is its base's half-space cut by the solid nappe.
3. *The octant.* A shape symmetric in a coordinate has a nearest point whose
   coordinate has the point's sign: reflecting a foot to that side brings it
   no farther.
4. *One root.* Each term of `F`, `G` over its interval and `H` falls
   strictly, so each has one root where its feet keep the point's signs.
   Eberly's "Distance from a Point to an Ellipse, an Ellipsoid, or a
   Hyperellipsoid" gives the ellipsoid's root and bracket and the zero
   branches, and Hart's "Distance to an Ellipsoid" (Graphics Gems IV) the
   same equation; neither was fetched for this design, and the engine's
   ellipse follows the first. The nappe's `G` and `H` are derived here.
5. *The capsule's regions.* Outside and under `y = 0`, the nearest point
   cannot lie on the cylinder, whose normals are level, nor on the upper
   cap, whose normals rise, so it is on the lower cap, where it is the
   ellipsoid's own nearest point by claim 3. Inside and under `y = 0`, a
   segment to the cylinder or to the upper cap leaves the ellipsoid first,
   and the ellipsoid's nearest point is on the lower cap. Between the planes
   and outside, the caps' normals point away, so the foot is on the
   cylinder at the point's own height.
6. *The far foot.* Between the planes and inside, the nearest point of a cap
   is on its equator, which the cylinder's depth already covers, or at a
   local minimum on its open half. Those are roots of `F` with
   `t + sy^2 < 0` and the other two denominators positive, an interval that
   is empty unless `sy` is under `sx` and `sz`. `F` is convex there, and a
   root is a local minimum exactly where `F` rises (the bordered Hessian's
   determinant is the product of the three denominators' signs times `F'`),
   so it is the larger root. A minimum the search misses lies within its
   last bracket of the saddle beside it, and from a saddle the distance
   falls to the equator, so the miss costs no more than that bracket's
   width in distance.
7. *The torus, outside.* The normal at a surface point is the unit torus's
   normal through the inverse scale, so a foot's point is
   `S u + m * S^-1 v` for the unit point `u` and its unit normal `v`, which
   gives `m`, `A` and `B`. The derivative of `A` in `c = cos a` is
   `r * (sx^2 - sy^2) / sx + sy * p.y / (sx * (1 - c^2)^1.5)`. Where `m`
   is not negative its second term is at least `sy^2 * r / sx`, so the
   derivative is at least `r * sx`; `B` likewise; `G` falls strictly.
8. *The torus, inside.* Every foot whose coordinates keep the point's signs
   is a root of `G` in the arc, or a zero of `A` or `B` where the point's
   coordinate is zero. The derivative above is even in `c` and rises with
   its size, so it changes sign at most once on each side, which gives the
   pieces. Bounds from monotone ends are exact bounds, so no root is in a
   discarded piece, and every candidate kept is a point of the surface.
9. *A zero coordinate.* The distance is 1-Lipschitz in the point, so the
   branch at a pole is the limit of the solve beside it, and a branch taken
   within a tolerance of zero moves the answer by no more than the
   tolerance.

**Where a solve may stop.** At every step a root lies in a bracket whose ends
have opposite signs, and that invariant carries the proof, not the count of
steps. Three interval rules follow from it.

- *Between the bracket's ends.* Each coordinate of a quadric foot is
  monotone in `t` (or `k`), so the true foot lies in the box spanned by the
  feet at the bracket's two ends, and the distance lies between the box's
  nearest and farthest points from `p`. For the torus the box comes from the
  end values of `A` and `B` on a monotone piece. This holds on both sides of
  every shape.
- *Outside a convex shape.* Claim 1's interval holds for any surface point,
  whatever produced it. The foot from the current estimate, put back on the
  surface, gives `n . (p - q)` below and `|p - q|` above, and the gap is of
  second order in the foot's error. In the prototype it fell under
  `1e-6` of the distance for 99 samples in 100 after twelve halvings, where
  the bracket alone needs twice that.
- *A faster step.* A Newton or secant step may replace a halving when it
  lands inside the bracket, since the bracket still holds the root.

A solve stops when its interval is narrower than its format resolves, or
sooner at a tolerance its caller's check justifies. It returns the interval's
lower end in size, so stopping early never reads farther than the surface is.
A stop on a count of steps alone is not allowed.

The fixed-point evaluator computes the interval in Q48.16 with outward
rounding (`FixedInterval`) and widens its box rule by the interval's width,
in place of the ellipse's fixed slack. The kernel runs the same steps in
float and carries no interval: its error is the format's, and a law holds it
to the fixed-point answer.

**The prototype.** A double-precision prototype of the capsule, the cone and
the torus ran against a brute-force closest point (a surface grid, then a
zoom about its best samples). Over 2,400 points, half of them inside, over
scales up to 6 to 1 and over flat-capped capsules and tall-tubed tori, no
solve read farther than the brute force by more than `3e-14`. The brute
force read farther than a solve by up to `7e-5` at points a few millimetres
from a rim, where its grid is the coarser of the two. 102 points on axes,
centres and symmetry planes agreed to `2e-6`, read as the limit claim 9
allows. Claim 1's interval held at every one of 11,599 early stops. Inside
tall tubes the scan found one root or three and never more, and the nearest
foot was always the first or the last. It decided four things: the search
runs over the tube angle (over its cosine the equator feet were lost at
`p.y` near zero); the bracket ends at a pole whether or not that coordinate
is zero; an azimuth with a zero coordinate takes its other component from
the unit circle; and the far foot is needed, since flat capsules were wrong
inside without it. The prototype was scratch and is not in the tree: the
laws of steps 2 to 6 replace it.

**Steps, each with its check.** The invariant is that a primitive
`SdfSolidGeometry.AppendScaledPrimitive` emits under any scale answers the
distance to its authored surface within its reported interval, in both
interpreters. Step 8 enforces it: that method is the one door a scaled
primitive is lowered through, and after it no arm of the method reaches a
`Scale` with unequal components. The guarantee stops at what is not a scaled
primitive: a superellipsoid past exponent 2 stays a gauge, warps, flares and
field ops stay bounds, a sweep and a path certify no gain, and a C# caller
can still write `SdfProgramBuilder.Scale` itself.

1. Correct the record: the stretched ellipsoid is a bound. The exhibits and
   this design are that step.
2. A sphere, ellipsoid or exponent-2 superellipsoid with two equal radii
   lowers to the ellipse revolved at offset zero, turned so its unequal axis
   is Y. Check: `puck creation shape` reads `distance=exact` for a spheroid
   and for the valley's `lake-basin`, `puck creation stats` still reports
   `contact: accepted`, and a spheroid cutter's `mesh audit --cpu` reads no
   triangle over the slope deficit's threshold.
3. One quadric root for both interpreters, with its interval. The ellipse
   becomes its two-term caller, so there is one implementation. Check:
   `EllipseDistanceLawTests` passes unchanged, and a law holds the three-term
   root to a double-precision reference inside, on each axis and plane,
   beside the surface and far from it, with the reference inside the
   interval at every stop.
4. The triaxial ellipsoid takes the root, and the gauge is left to exponents
   past 2. Check: `shape-sphere-xyz` and `shape-ellipsoid-xyz` read
   `distance=exact`, the box rule encloses the point answers
   (`TryDistanceBounds` over boxes about the law's points), and the two
   interpreters agree over a lattice.
5. The capsule. Check: `shape-capsule-xz` reads `distance=exact` and
   `contact=admitted`, and the law covers capsules with `sy` under `sx` and
   `sz`, where the far foot decides.
6. The cone, then the torus, each with the same law and the same two reads.
   The torus's law covers `sy` above `sx` or `sz`, and `p.y` at and near
   zero.
7. The round cone. Its first task is the inside: the capsule's claims 5 and
   6 with each cap ending at its tangent plane instead of its equator,
   proved and run against brute force as the capsule's were. Until then it
   keeps its bound and `puck creation shape` says so.
8. `AppendScaledPrimitive` loses its `Scale` arm for unequal components, and
   `SdfSolidGeometry.DilationGain` its stretched case. Check:
   `puck creation shape` over the stretched row prints `bound` for
   `shape-superellipsoid-xyz` alone.
9. The exhibits and the cost. `puck mesh audit --cpu` reads no triangle over
   the slope deficit's threshold, no fold and no flip on
   `shape-stretch-cut` and `shape-stretch-smooth`, and the smooth cut's
   fillet reaches 40 mm from each rim. Then, in the lead's window: the same
   audit over the stored bakes on both backends, captures of both exhibits
   and of the lake's bowl, and the field work on the bake's `done` line
   before and after. That work decides whether the kernel takes the early
   stop and the faster step, and whether a program of hard unions alone may
   keep a bound, which would need its own proof that nothing in it reads a
   distance.

Each new shape is an instruction-set change with every partner in one change
([the checklist](../../.claude/skills/rendering/SKILL.md#changing-the-instruction-set)):
the kernel, the fixed-point evaluator with its bounds and reach rules, the
shading cost row beside the ellipse's (`SdfShadeCost`), and the contact
lowering, which reads the same shape.

**What a review should attack.**

- Claim 5's inside case: that no point of the cylinder or the far cap is
  nearer than the near ellipsoid's own nearest point.
- Claim 6: that the open cap holds at most one local minimum, that it is the
  larger root, and the bound on a missed one.
- Claim 7's derivative and claim 1's use for the torus: that outside the
  hull's flat faces the hull's nearest point is on the torus.
- Claim 8: that the pieces are complete, including where `A` or `B` is not
  positive, and that three is the most roots a tall tube has. The prototype
  saw no more; nothing proves it, and the enumeration does not depend on it.
- The cone's base rule: that a nappe foot below the base leaves only the
  base's disk and rim.
- The zero branches of the nappe and the torus, which are derived here and
  have no published form to check against.
- The returned lower end: that it is still zero on the surface and keeps the
  field 1-Lipschitz across the regions' seams.

#### Glass

The [glass handbook](../rendering/sdf/handbook/frame-rendering.md#glass-after-the-opaque-image)
owns the implemented path. Remaining slices are:

- The owner's paired-backend look at `shape-glass`: depth and thickness,
  frost blur, refraction and the `glass`/`glass-shade` work readings.
- More than one glass layer per pixel when a scene stacks panes.
- Compose refracted, tinted sky within the glass path.
- Glass shadows, occlusion and coloured transmission/bounce into irradiance.

#### GPU submission and routed worlds

The [engine job system](open-items.md#engine-job-system) owns frame scheduling
and recording. The remaining GPU and routed-world investigations are:

Every raster pass selects its draws on the GPU through `SdfMeshOcclusion`
([contract](../../.claude/skills/rendering/SKILL.md#far-cascade-caster-selection)).
Shared geometry instancing is a separate extension the
[visibility plan](visibility.md#the-build-order) owns (ordered compaction and
instance buckets): the meadow draws many copies of few geometries, and sparse
visibility, per-draw level choice and stable depth-tie ordering need an
instance map and per-geometry, per-level ranges, never `instanceCount` alone.

- Re-measure RTX 2060 pixel passes at matched clocks before attributing a
  Vulkan/Direct3D 12 gap. Inspect surface and resolve register pressure and
  occupancy; measure geometry instancing in the indirect records rather
  than repeating the slower one-draw vertex-search experiment.
- Reopen either unattributed Vulkan device-loss case on recurrence: the
  first-frame fault whose last checkpoint is `glass-clear`, or the fault as
  Nsight GPU Trace begins whose checkpoint is root `place`. Repeated successful
  boots do not establish a cause; retain fresh fault and validation logs.
- Measure front-to-back order or a depth prepass against mesh overdraw,
  retained descriptor writes for Vulkan pane sources, batching graph command
  lists into fewer submissions and queue idle time.
- Keep graph targets across a crossing where extents and ownership allow it,
  then re-measure Vulkan's first submissions after reconfiguration. The shared
  GPU memory arenas already exist.
- Complete clipped straddling for catalog avatars, prototype mesh draws and
  cross-world bodies using the existing portal carry and clip mechanisms.
- Make a presented world's probes world-scoped: ring and camera-export keys,
  per-socket refusal without a constructor exception, local-device sockets
  refused by name, and lifetime following the presented world. Remote screen
  sessions, machine frames and text also need their transport and font origin.
- Measure live residency and view-target bytes on the RTX 2060, including
  device-local and host-visible-device-local usage and Vulkan aperture heaps.
  Share tables for the same mirror and stage infrequently changed data where
  the measured budget calls for it.
- A package pass takes its extent from its first image port. Resolve the
  output-extent contract for a pass whose only image is a fixed-size atlas.
- Account a graph's storage by lifetime before reusing any of it. The plan
  forbids unrelated storage aliasing today, and no allocator proves a handoff.
  First, from the plan's first and last uses and the actual dimensions and
  formats, compute the unaliased bytes and a conservative estimate of what
  compatible, non-overlapping transients could share, by lifetime class
  (external, published, history or retained, frame-slot, transient, retired,
  background), with flight-slot multiplicity and graph-replacement overlap
  counted and no reference summed twice
  ([how memory is read](streaming-and-memory.md#what-adds-and-what-only-names)).
  Only if that estimate is large: assign physical allocations to eligible
  transient chains in the compiler's immutable plan, within one node and queue
  first. The invariant is that reuse is legal only after every GPU access to
  the old allocation and every external lease has ended, which the compiler
  can prove locally and the allocator's retirement door must enforce; history,
  held portal images, capture inputs, standing pass contents and background
  bindings are never eligible. Check: planned against actual peak allocation
  across resize, graph replacement, held views, capture and background work on
  both backends, kept only for a measured byte dividend.

The back of a portal's glass still needs a presentation choice: no image,
fallback colour or a front-image mirror. The
[owner decisions](open-items.md#owner-decisions) hold that choice.

### P15 — Temporal reconstruction

Reconstruction and dynamic resolution are implemented. P15-8's floor-tier
tuning is removed; the Steam Deck observation below runs at `high`.
The [temporal packet](open-items.md#temporal-shimmer-and-cadence-flicker) owns
remaining image work. Dynamic resolution follows GPU frame timing, with present
timing as its fallback; without timing or a display period it holds its grid
([frame handbook](../rendering/sdf/handbook/frame-rendering.md)).

### P18 — Sky and atmosphere

Only the celestial `bodies` vocabulary and the remaining infinity-view image
and admission observations stay here. The [frame handbook](../rendering/sdf/handbook/frame-rendering.md#the-sky-once-and-a-composite-last)
owns clocks, the open layer stack, atmosphere and artist controls.
[Author leaks](open-items.md#author-leaks-to-the-standard) owns the move of
sky and quality presets into `worlds/standard/`.

P18-7 adds `render.sky.bodies`: discs, crescents and rings; directions,
orbits, keys and state-row motion; binding to named lights; and illumination
by other bodies. A body becomes a shadow candidate through its bound light.
Appearance and illumination remain distinct from the existing shadow-slot
selection and rasterized cascade path. Per-light penumbra stays on the light.
Remove the sun-disc-specific binding and fallback directions when their callers
use bodies: `WorldRenderSkyLayer.SunDisc`, `SdfSkyDisc.Light`,
`worldSunDirection`, `SdfSunDirection` and `DefaultSunDirection`. Show two
moving bodies lighting one another and their named lights casting the expected
cascade shadows in the debug world, with stable identities through a reorder.

P18-11 part two uses the existing `view`/`far` layer vocabulary, binder, final
per-view fits, image routing and completed shown-texel demand. Its remainder is:

- A `sky-portal` image and discriminator: the destination turns with the viewer
  and never translates with them; an unseen layer stops rendering on the next
  frame; a far instance renders only its angular rectangle.
- Actual admission at the infinity-view cap and named refusal one past it,
  including live edits, with count and memory visible in `world.budget`.
- Windowed observations of the live external source against capture fill and
  an independent nonexternal control, including withholding when a source
  never supplies the safe fill. Nesting retains the existing depth fallback.

Read actual work at `high` beside frame timing. These observations introduce no
floor-tier counter matrix, recorded ceiling or shadow-march path.

### P19 — Neighbour-content bounds for wallpaper folds

`SdfWallpaperFold.IsContinuous` admits PMM, P4M, P3M1 and P6M. The other thirteen
groups (P1, P2, PM, PG, CM, PMG, PGG, CMM, P4, P4G, P3, P31M and P6) jump at
cell walls or rotation seams: sampling only the local copy can miss a nearer
neighbour. Their admission needs both the CPU queries in `Puck.SignedDistance`
and the bake lattice to bound content across those discontinuities.

Derive a tight local content box widened over every in-cell image, including
P4G's offset mirror and the hexagonal sector images. Bound distance to the
neighbouring cells' content, and certify a region crossing a wall or sector
seam against all content that can affect it. Content touching a wall still
needs a certified crossing or a named refusal. Consult `FixedInterval` and the
existing shape bounds before adding per-shape logic; a loose sphere is not a
replacement for a tight tile box. Store the bound in a side table rather than
assuming spare instruction lanes.

Complete this work when all seventeen groups accept off-centre prototypes,
CPU distance and region-query results agree with explicit neighbouring copies,
and bake samples and contours remain correct across cell walls and sector
seams. Exercise them in the debug gallery on both backends.

### P20 — Visibility, areas and residency

Per-pass box culling, depth occlusion, rated presentation, planar merging,
quadric collapse, static-union chunks, per-view light range selection and
near-first mesh demand are implemented. The
[streaming plan](streaming-and-memory.md) owns lighting on the same demand (its
decision 9), mapped portal demand, byte-bounded eviction, authored areas and
terrain tiles.
The [engine job system](open-items.md#engine-job-system) owns execution and
memory ownership; the [rendering decision](../decisions/rendering.md#a-raster-pass-draws-only-what-it-can-show)
owns the one-display-pixel detail rule. How the frame decides what to draw
and submits it (compaction, instanced buckets, authored motion on the GPU,
geometry at the source, the early-depth variant and multi-view selection) is
[the visibility plan](visibility.md), with its build order and the
measurements that order it.

Remaining observations specific to visibility are:

- On a quiet machine compare `world.cull off`, `frustum` and `occlusion` in the
  lobby, aisle, hall and open entrance, using `host.frames`, mesh and cascade
  timings. Isolate the one-code lit-image difference between box and occlusion
  culling even when their geometry matches.
- On a display presenting at least 60 Hz, compare `world.rates on`/`off`, the
  far cascade averaged over held and rendered frames, nested views and pose
  work. The VRR play observation uses `world.target vrr`, `world.rates` and
  `debug.go range`; judge held bodies, screens and windows for stepping under
  the 30 and 15 Hz tiers. The tiers themselves are settled
  ([presentation rates stay tiers](../decisions/residency.md#only-presentation-follows-the-observer)):
  a continuous rate is reopened only by an observation the tiers cannot
  explain, named with it.
- Camera views filmed onto in-world screens still render at the consumer's
  rate: the screen lever rates source instances, and `WorldViewGraphHost.Rates`
  does not look up a camera view's glass as it does a session's.
- Choose boot presentation from measured memory, cores, display pacing and
  frame time while retaining the highest quality. Qualify the Deck's Vulkan
  path on the device at 45 FPS.

The far cascade's snap is never coarsened to save frame time: a coarser snap
gives up shadow resolution and cannot repair a missed frame bar. A cascade
held between changes is a different thing and is still planned, for terrain
alone: the [terrain pass](terrain-pass.md#distance)'s far-shadow step is its
one home.

## Deferred to the end

These observations need a device, display or operating-system interaction.
They run on the debug world at `high`, with counted work beside quiet-machine
worst-frame timing, and keep their packages open until observed.

- P1a: a windowed boot without a usable GPU driver exits 2 with the named
  unsupported result, including render-root teardown.
- P1b: qualification on the reference AMD devices and a driver-initiated
  removal/recovery exercise. Record actual peak device-local memory alongside
  timing; the RTX 4070 observations do not qualify another device.
- P10 and sky/GI: paired-backend hardware observations at `high` on the
  RTX 2060, including live and retiring residency/graph bytes and aperture
  usage. The frame remains under 16.7 ms; lower-tier observations cannot close it.
- P12b-4: a real camera feeds a screen on both backends.
- P13b-4: a real Windows editor receives the click at the mapped point, and the
  focus-return chord returns input to the game.
- P15: the Steam Deck runs the debug world at the highest quality with every
  frame under 22.2 ms; inspect reconstruction during motion. Dynamic-resolution
  behaviour is observed separately and cannot lower the qualification quality.
- P16: an HDR display reports the HDR swapchain colour space; a ramp exceeds
  SDR white; HDR desktop capture stays unclipped; toggling HDR reopens a still
  or changing captured desktop in the new encoding; and UI stays at paper white.
- Both backends: a final review of matching captures and device readings,
  with settled bakes and irradiance and matching camera/lighting state.

Later display work is HDR calibration, per-display metadata and Linux HDR on
the Steam Deck OLED. Later source work is PipeWire DMA-BUF capture and V4L2
camera producers.

---

[Plans](README.md) · [Decisions](../decisions/rendering.md)
