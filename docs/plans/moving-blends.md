# Blends between parts that move

A smooth blend between two parts of an animated creation that move against each
other (the swallow's head glancing on its body) tears in motion. This memo sets
out how such a seam holds under continuous motion and recommends one design,
reviewed before code. The Moth's rebuild relies on whatever lands here, so each
step names its owner type and the check that shows it worked.

Steps 1 to 3 are built: a skinned group bakes at its authored rest, each vertex
rides one owner rigidly or two through a normalized dual-quaternion blend, and a
blend past its admitted envelope is split and bakes as a crease, which PUCK122
reports. The rendering skill's
[mesh-bake reference](../../.claude/skills/rendering/references/mesh-bake.md#groups-poses-and-materials)
owns the built contract. What remains is step 4 (paints and lanes across the
seam), step 5 (the cost reading), the envelope published as a bound, and gap 8
(a cutter that moves against the surface it cuts), whose first step deletes the
settle mechanism. The tags (F1) to (F9) in the headings name the
design review's findings each section answers.

## The invariants any answer keeps

- Every view draws baked meshes, with no field walk in the frame. The bake is
  the only GPU kernel that walks the field, through one `mapCore` call site (the
  [rendering skill](../../.claude/skills/rendering/SKILL.md#editing-kernels)).
- A mesh, and everything stored with it, is a function of its geometry
  (`SdfMeshGeometryBuilder`).
- A visible surface never tears and never folds, at any pose the document
  admits. Where a blend cannot hold that, the author is told at compile time
  and sees a crease, never a tear.

## Options

1. **Settle rebake alone.** A time-driven idle never holds, so it stays torn.
   Rejected, and deleted.
2. **A bank of baked poses.** Bake work and bytes grow with the pose space, and
   independent contours share no vertices to interpolate between, so swaps pop.
   Rejected.
3. **A rigid part per owner (a crease).** Each moving part bakes alone. It holds
   at every pose, but it draws a crease, not a blend. Kept as the fallback for
   motion outside the admitted envelope.
4. **A blend zone evaluated in the vertex or pixel stage** from both parts'
   fields. This puts a field walk in the frame. Rejected under the first
   invariant ([rendering decisions](../decisions/rendering.md)).
5. **Continuous seam rebakes.** Each one costs frames of dispatch, readback,
   contour and landing (`SdfGpuWork`), so the seam lags the motion and pops.
   Rejected as the baseline.
6. **A skinned seam inside an admitted motion envelope (recommended).** Each
   vertex in a blend's zone carries two owners, the share between them and that
   share's gradient. The vertex stage blends the two owners' rigid motions by
   dual quaternion, with a normal correction from the gradient. Outside the
   envelope the compiler falls back to option 3 and says so.

## The recommended design

### Admitted motion envelope (F1)

Fixed topology can follow a blend only while the parts stay close. The compiler
computes, for every smooth blend joining shapes with different motion owners
(PUCK122's pairs), an envelope from the document alone:

- **Relative rotation** θ: the sum of swing amplitudes along the parent chains
  from the two owners up to their common ancestor
  (`ShapeSwingDocument.Amplitude`, at most π each). Frames contribute their
  finite poses (`CreationMotion.FrameMotion`). A `Linear` swing, an effector
  chain, or a driver whose `weightBy` curve is not bounded by 1 at compile time
  has no finite bound.
- **Relative displacement** D at the seam: the slide amplitudes along those
  chains, plus `2 R sin(θ/2)`, where R is the farthest distance from a swing
  pivot to the seam box. The seam box is the intersection of the two shapes'
  part boxes (`SdfInstancePart`), each inflated by the blend's smooth radius k.

A blend is admitted when θ ≤ 60° and D ≤ k / 2. The k / 2 bound is what keeps
the skin from folding: the skin's Jacobian is `R (I + u vᵀ)` with
`u = Rᵀ Δp` (the two rigid images' separation, |Δp| ≤ D) and `v = ∇w` (the
share's gradient, |∇w| ≤ 1/k for 1-Lipschitz operands, from the smooth union's
`h = 0.5 + 0.5 (b − a) / k` in `field/sdf-blend.hlsli`). Its determinant is
`1 + u·v ≥ 1 − D/k ≥ 1/2`, so no triangle inverts. The posed surface stays
within D of the posed field's blend, which stays within k of both parts, so the
bridge never outlives the field's own connection.

Past the envelope, the group is split at that blend: the moving owner's shapes
bake as their own rigid group (option 3). PUCK122 changes from a stretch
warning to a statement of the outcome: the creation, the two shapes, θ and D
against their bounds, and "bakes as a crease; widen `smooth` to at least 2D or
reduce the motion to keep the blend". It stays a warning, since a crease is a
correct drawing, and `allow PUCK122` acknowledges it.

Worked estimate (unverified, from the draft swallow's numbers): the head
glance's 0.55 rad (θ = 31.5°) about a pivot roughly 20 mm from the throat seam
gives D ≈ 11 mm, past k/2 = 7 mm for the head's 14 mm smooth. The draft would
crease until its author widens `smooth` to about 22 mm or lowers the amplitude
to about 0.35 rad.

### The rotation branch (F2)

Skinning blends only motions relative to the group's own slot, never world
poses: each owner's transform is expressed in the group's frame (the slot the
group rides), so a whole body turning arbitrarily contributes nothing to the
blend. Inside the envelope each owner's relative rotation from the authored
rest is at most θ ≤ 60°, so two owners differ by at most 120° and their
quaternions' dot product is at least cos 60° = 0.5. The sign rule: take each
owner's quaternion in the hemisphere of the first owner's (`dot ≥ 0`). Inside
the envelope that choice never switches, so no seam pops, and the blended real
part's norm stays at least 0.5, so normalization is never singular. Previous
positions use the same rule on the previous transforms, which are inside the
envelope too. A pose outside the envelope cannot reach the skin, because such a
blend was split at compile time.

### Baking at the authored rest (F8)

The skin bakes every member at its authored rest pose, not the arrival pose.
A member slot carries its parent's delta frame (`WorldCreationInstances.Emit`),
so at rest every member's pose relative to the group's slot is the identity.
`SdfMeshGeometryBuilder` keys a skinned group's other slots at that identity
pose. Unverified premise, checked in step 1: rest is the identity delta for
every owner kind (swing, slide, frame).

This removes the settle rebake's reason to exist. A skin's error is bounded by
the envelope at every pose, and the reference pose no longer depends on when
the program arrived, so `TryTakeForm` reuse is exact. The settle rebake and its
pop are deleted: every moving blend is inside the envelope (skinned), split
(rigid), or a moving cutter read where it stands (an operand, gap 8). This
narrows the continuity guarantee honestly. Within one baked mesh, motion is continuous. A
document edit or program rebuild still replaces meshes, as any rebake does.

### Influences: computed per place, through composition, on the CPU (F4, F5)

- **Per place (F4).** A place is a position; a crease or material cut gives it
  several vertex copies (`SdfMeshMaterialCut`). The skin round computes one
  influence tuple per place, at the place's exact position with no normal
  lift, and writes it to every copy. Copies at one position therefore move
  identically under any motion, so a crease never cracks. The winning-slot
  member moves to the same rule; today's lifted sample can already give two
  copies different members. Check: the audit counts places whose copies'
  tuples differ, and it must count zero.
- **Through composition (F5).** The share is not the last blend's local `h`.
  It is propagated through every composition, the way the field itself is: each
  accumulator carries an influence vector over owners. A blend of accumulator
  `(acc, I_acc)` with candidate `(c, I_c)` gives
  `I = (1 − s) I_acc + s I_c`, with s the candidate's share (`1 − h` for smooth
  union, the analogous factor for smooth subtraction and smooth intersection,
  0 or 1 for the hard forms). A scope's result composes as a candidate carrying
  its own vector. For smoothMin(smoothMin(A, B), C) at halves, this gives A ¼,
  B ¼, C ½, and owners sum their leaves. Chamfer, stairs, morph and other blends
  with no share are admitted only between shapes of one owner; across owners
  they split (option 3).
- **On the CPU.** The propagation runs in `SdfFieldEvaluator`'s render reading
  over the group's sealed instructions, at each place, on the bake's CPU pool.
  This leaves `mapCore` and its register budget untouched. A group whose field
  the evaluator refuses (a glyph, a regular polygon, a sampled region) splits.
  The per-place cost is one group evaluation, about the work of today's skin
  points round.
- **Two owners a vertex.** The vector is truncated to its two largest owners,
  renormalized. A place where a third owner's share exceeds 1/64 cannot be
  represented. The bake then splits that group and logs it by name
  (`SdfMeshBakeReport`). The compiler cannot see this case, because it depends
  on where three owners' surfaces meet.

### Backends (the "identical" contract)

The influences are a deterministic fixed-point function of the sealed geometry
and the mesh's positions. They are bitwise identical for identical meshes on
every machine. The meshes themselves come from each backend's bake, as today,
so across backends the contract is:

- the same function, so equal counted work: the same places count;
- bitwise identical influences wherever the two backends' meshes are identical;
- visually equivalent posed surfaces wherever the meshes are not, judged by
  captures.

The posed blend is float GPU arithmetic, so it is presentation only, and it
sits outside rule 4 like the rest of the frame. Bitwise identity of posed
positions across backends is not claimed. Demanding it would mean
software-emulating the vertex stage for no visible gain.

### Normals (F3)

A blended rigid rotation of the rest normal misses the seam's own bend: two
translating parts give `p' = p + w(p) t`, whose normal tilts by `t ∇w`. The
shading normal uses the Jacobian above. By the Sherman–Morrison inverse,
`n' ∝ R_w (n − v (u·n) / (1 + u·v))`, with `R_w` the blended rotation, `u` the
two rigid images' separation in the rest frame and `v = ∇w`. The bake stores ∇w
per blend-zone vertex: it is the propagated share's gradient, from the
evaluator's gradient reading. The envelope keeps `1 + u·v ≥ 1/2`, so the
correction never divides by a small number. Curvature (`sdfMeshMeanCurvature`)
reads the corrected normals, so it follows.

### Bake post-passes (F6)

Every pass that rebuilds a skinned mesh carries the influence arrays and keeps
them sound:

- **Planar merge** (`SdfMeshPlanarMerge`): eligibility and region growth
  already compare the one member index. They also compare the whole tuple (two
  owners, share, gradient) bit for bit, and a vertex whose share lies strictly
  between 0 and 1 is never removed: a rest-planar face in a blend zone bends
  when posed. `Build`'s compaction remaps every influence array with
  `VertexMembers`.
- **Collapse** already skips meshes with members (`SdfMeshCollapse.Levels`).
- **Chunks** apply only to static groups.
- **Store and codec** (`SdfMeshStoreCodec`) persist the arrays as delta-coded
  planes, as they persist `VertexMembers`.

### Paints and lanes across the seam (F7)

Material coordinates follow the skin rather than one rigid slot. A mesh's
vertex positions are already its rest positions in the group's frame. The
surface pass interpolates them at the hit's barycentrics, which gives the hit's
rest point, and maps that point into a layer's parent frame through that
parent's baked pose (`SdfMesh.Members` holds it). This replaces
`sdfLayerFrame`'s inverse of the live slot transform. For a vertex with one
owner the result is unchanged, since the live inverse undoes the live forward
transform. Across a seam, paints, insets and weathering deform with the
surface: nothing swims, and nothing jumps at the nearest-vertex boundary. Lanes
are scalars, and they blend by the hit's interpolated shares. The glass pass
takes the same rule through the shared helpers.

### Cost against the frame bar (F9)

- **Per vertex resolve.** A blend-zone vertex costs two transform fetches, a
  dual-quaternion blend, a normalization and the normal correction, roughly
  three times today's rigid resolve. A vertex whose share is 0 or 1 keeps
  today's rigid path, behind a branch that is coherent across a triangle. Blend
  zones are bands about k wide along seams, a small share of a body's
  vertices.
- **Per pixel.** The surface pass resolves three positions and three normals
  for each covered pixel (`sdfMeshSurfaceNormal`), and reprojection resolves
  three current and three previous positions (`sdfPreviousPoint`, and again in
  the resolve pass). One per-triangle resolve helper returns all of them once
  per pixel, halving those resolutions. Only pixels of skinned draws whose
  triangle touches a blend zone pay the blended arithmetic.
- **Memory.** A skinned vertex gains a packed word (a second member byte and a
  16-bit unorm share) and three 16-bit gradient components: 12 bytes on the GPU,
  beside today's 32-bit member index. A lighter layout (influences only on
  blend-zone vertices, through an index) is possible if measured bytes call for
  it.
- **Counted model.** Extra work is (skinned covered pixels whose triangle is in
  a blend zone) × 6 blended resolves + (blend-zone vertices) × raster passes.
  `world.counters gpu pass sdf.world$surface` counts the texels.
- **Measurement owned by step 5.** A quiet-machine window on both backends on
  the RTX 2060 measures a full-screen skinned close-up and a crowd of skinned
  bodies against the 60 FPS bar. If the close-up misses, the cost is cut at the
  cause (the per-triangle helper, or resolving positions once into the
  visibility record), never by dropping the normal correction.

## Engine gaps first

Two of the engine gaps in the
[rendering plan's list](rendering-research.md#engine-gaps-filled-first) are
this plan's, and each is filled before the work that sculpts or tunes around
it (the owner's ruling, [AGENTS.md](../../AGENTS.md#pucks-purpose)):

- **Gap 10: a paint does not follow a blended seam between parts that move.**
  Material coordinates across a blended seam are not blended, so a painted
  marking on the Moth's or the swallow's head swims. This is step 4 below, the
  next step of this plan, and it goes first. The two creations wait for the
  owner's pair session, so the step's own check is the striped palette across a
  moving neck, not either creation.
- **Gap 8: a creation with a cutter that moves against the surface it cuts
  takes no relief.** The design is
  [its own section below](#gap-8-a-cutter-that-moves-against-the-surface-it-cuts).
  Its first step is built: the cutter is an operand of the group it cuts, and
  the settle rebake is deleted, so step 5 is only its cost window.

## Steps, each with the check that shows it worked

### What is built, and its invariants

The invariant is enforced at the pooled emission and mesh boundaries: only delta-frame
instances opt into `SdfInstanceRange.RestPoseSkin`; `SdfMeshGeometryBuilder` freezes those
slots, and the motion plan is the emitter's single partition. No presentation result
feeds simulation state. Other dynamic instances retain their absolute-pose semantics.

Three invariants hold the skin, each behind one door:

- **A skin is a function of its geometry.** `SdfMeshBakeGroup` seals a skinned group with
  an `SdfMeshSkinSource` taken from the program it was planned from; `SdfMeshSkin.Build`
  and `Split` take only that source, and its constructor is internal to the seal. A group
  kept across a restart, or one still contouring after its program left, reads its own
  instructions, never the program the bake carries now. The source copies the group's
  instructions on first reading and lets the program go then or when the group's
  workspace is released.
- **A vertex wholly one owner's rides that owner first, with no share.**
  `SdfMeshSkinTuple.Of` collapses a share that rounds to either end onto the owner that
  holds the vertex, with `SdfMeshInfluence.Rigid`; `SdfMeshInfluence`'s constructor is
  internal and the `SdfMesh` constructor refuses any tuple that is not
  `SdfMeshInfluence.Canonical`. Such vertices take the vertex stage's rigid path, and
  their material layers shade in their owner's frame (`sdfMeshSurfaceNormal`'s frame slot).
- **A carve one owner makes in another stays cut in every fallback.** The third-owner
  split keeps every carving shape (`SdfBoundAlgebra.Carves`) in every member's part
  (`SdfMeshAudit.MemberFields`), and a partitioned group borrows every other owner's carve
  (`CreationMotionPlan.Borrows`), riding its own slot. A carve that moves with the surface
  it cuts is exact; one whose owner moves alone and only cuts is an operand, read where it
  stands (gap 8's G1, below).

Two shapes move apart only by motion below their nearest common ancestor
(`CreationMotion.MovingBlends`): a swing, slide or effector on either side, or timeline
frames that carry the two sides by different composed motions. Shapes one frame moves
together, such as the Moth's eye whites and their openings in the blink, join without a
crease. The envelope's frame term is each frame's largest separation of the two sides'
composed motions over the seam box's corners, and each swing's reach grows by the
displacement counted before it, so the bound holds for swings about different pivots.

The conservative partition is per authored group: any rejected pair splits the group by
actual motion owner. Every diagnostic reports that group outcome, even for another pair
whose own envelope fits. Empty inflated-box intersections contribute no seam bound.
Followers and timeline descendants inherit their actual parent's slot. Unbounded
followers cannot enter an admitted blend.

Step 4 is next: material coordinates and blended lanes. Steps 1–3 do not claim that
paints already follow the seam. A skin bakes once, at the pose its program arrived in;
no group is baked again because its members settled.

### Lead verification request

Prepare the debug-based admitted source described below. Start the lane checkout with
`puck_world_start`, `document: "<verification-source>"`,
`windowed: true`, `holder: "moving-blends verification"`, once with `backend: "directx"`
and once with `backend: "vulkan"`. Use the shared mesh store, not a cold cache.
The creation is the real `swallow` at the perch, framed by the following pose script.
`worlds/debug/debug.puck` does not currently import `birds.puck`, so starting the
ordinary package alone cannot show this witness.

```text
view.override camera look 3.3 1.22 3.62 3 1.15 4 30
world.row.set creations swallow document.shapes[name=head].swings [{"driver":"glance","axis":[0,1,0],"amplitude":0.02,"pivot":[0,0.06,0.03],"wave":"sine","phase":0}]
world.row.set creations swallow document.shapes[name=tailBase].swings []
world.row.set creations swallow document.drivers [{"name":"glance","signal":"time","cadence":6.2831853},{"name":"bob","signal":"time","cadence":1.4}]
world.wait bakes 120
world.wait residency
world.screenshot <absolute-path>/moving-blends.png presented 60
world.wait 120
```

This is the admitted envelope fixture, not a change to the bird's shipped look. The
glance sweeps its entire admitted ±0.02-radian range in a second; the tail's motion
owner is removed to keep the witness on two owners. A zero-amplitude swing still
reserves an owner and can trigger the third-share fallback. Require 60 completed capture receipts and inspect
the reported ticks: the asynchronous burst must span both extremes and the return.
If it does not, repeat the burst while the glance keeps cycling. Inspect throat and
wing intersections from the portrait and the reverse view, with no spike, crack or
fold. Repeat with `world.debug-view motion`: still body parts must have zero motion,
while the head follows the glance. Stop each process with `puck_world_stop`.

The CPU counterpart uses `worlds/debug/birds.puck` prefixed with
`schema: "puck.world.definition.v1"` and `basis: "../../worlds/debug/debug"`, written
to a temporary source two directories below the repository root (`<verification-source>`
below). Set the head amplitude to `0.02`,
remove the tail's `swings` block, and set the glance cadence to `6.2831853`.
The head then owns slot offset 1. Pose about the engine-frame pivot [0, 0.06, −0.03]:
`t = pivot − R pivot`, not a rotation about the origin. Run:

```text
puck mesh audit --cpu --world <verification-source> --prototype swallow --top 1 --worst 0
puck mesh audit --cpu --world <verification-source> --prototype swallow --top 1 --worst 0 --pose '1=0.0005999600008,0,-0.0000059998,0,0.0099998333,0,0.9999500004'
puck mesh audit --cpu --world <verification-source> --prototype swallow --top 1 --worst 0 --pose '1=-0.0005999600008,0,-0.0000059998,0,-0.0099998333,0,0.9999500004'
```

Each run prints the posed field error, which must stay under the head/body D bound
(`compile --validate` prints θ and D for the pair), and the skin line: zero split-tuple
places, and the head's own vertices counted as rigid member vertices, not blended. The
error includes the rest contour's error, not only skin error, and the CPU audit alone
does not establish the visual bar. The figures each run produced are in the commits that
ran them.

The rendering skill carries a capability eval, `moving-blend-crease-copies`, for the
lead's eval window.

4. **Material coordinates.** The rest-point mapping for paints, insets and
   weathering, and blended lanes, in the opaque and glass passes. Check: a
   striped palette across a moving neck, captured in a presented burst, shows
   stripes fixed to the surface.
5. **Cost measured.** Run the quiet window above; the settle rebake it once
   waited on is deleted ([below](#relation-to-step-5-the-settle-rebake)).
   Check: the frame bar holds in the close-up and the crowd on both backends.

6. **The envelope as a bound.** The group's admitted separation `D` is published
   in the mesh and its record, so a blended draw's box can be its members' boxes
   widened by it ([virtual geometry](virtual-geometry.md#d2-bounds-for-skinned-and-multi-slot-draws)'s
   G3b). Check: the posed audit at the envelope's extremes tests every resolved
   vertex of a blended group against that box and prints the largest excursion
   beside `D`; none exceeds it.

## Gap 8: a cutter that moves against the surface it cuts

A cutter is a shape that removes material: a subtraction, or an intersection,
in the creation's field (`SdfBoundAlgebra.Carves`). The gap is a cutter whose
motion differs from the surface it cuts: a glacier's front walking up its ice,
a door carved into rock that opens, erosion eating a bank. This section says
what blocks it in the code, then designs a fix that re-bakes only the part of
the surface the cutter actually reaches, and resolves the conflict with step 5.

### What the gap is in the code

The gap was first written up as a glacier that melts by sinking and carries
no relief. Neither holds today. The glacier is the standard
template `glacierMass` (`worlds/standard/water.puck`), placed by
`worlds/debug/water-scenery.puck`. Its ice is rounded serac blocks and snow
caps with swept crevasses cut out of them, and it shrinks by
`sizeBy: state.glacierLength` about its head, so the toe retreats as the whole
creation scales. Relief on a creation that moves as a whole already works: the
pooled emitter bakes such a creation as one composed field in the root's units
(`WorldPlacementStamper.EmitRootSurface`). The look critic rejected that
shrink, because it reads as an object shrinking, not ice wasting back. Ice that
wastes back needs a cut that moves up the ice. Two restrictions stopped that;
G1 lifted the second, and the first is G2's.

1. **A moving cutter turns off relief.** `CreationMotionPlan` throws when a
   creation has `noise` and any shape moves independently ("Noise relief
   requires every shape to ride the creation root; independently moving parts
   cannot share its field."), and the world validator refuses the same document
   (PUCK030). A probe (a relieved box with a sphere cutter sliding through it)
   gives, under `puck compile --validate`:

   ```text
   error PUCK030: prototypes[507].doc.noise requires motion of the creation as a whole — frames, shape drivers or effectors that move its parts cannot share one relief field.
   ```

2. **A moving cutter kept the cut it made at rest.** A hard subtraction has
   no continuous share between its two sides, so its envelope was refused and
   the group split by motion owner, each partition borrowing the cutter at
   rest. G1 made such a cutter an operand of its group (below); the same probe
   without relief now validates with this warning:

   ```text
   warning PUCK122: prototypes[507].doc.shapes[1].blend: creation 'gap8-slab' joins shape 'cutter' to group 1's shape 'ice' by Subtraction: cutter 'cutter' moves alone and cuts as an operand; the group bakes again each time it moves a step of the group's tier cell, so the cut trails it by at most one step, and contact still reads the cut at rest.
   ```

Two things the gap entry named are not the cause:

- **A static group that reads a moving slot.** Such a group exists in the
  geometry (`SdfMeshGeometryBuilder.ReadsSlots`), but it is planned again only
  when a new program arrives (`SdfMeshPathBake.Restart`), never as the slot
  moves.
- **The slide cap.** A slide's amplitude is capped at four creation units
  (`ShapeSlideDocument.MaxAmplitude`) because a slide past it would leave the
  placement's render bound. A `linear` wave on a `state.<row>` driver already
  reaches any distance through the driver's cadence, so the cap is an authoring
  nuisance. It also has no reason to apply to a cutter: a cut never grows the
  surface it cuts.

The proportional answer matters because the glacier is one group. A CPU bake
of it (`puck mesh audit --cpu --world worlds/debug/debug.puck --prototype
glacier --tier 6`) plans one dynamic, unchunked group and reports about 5.3
million samples and 262,000 triangles at the 64 mm tier, the tier a view from
the valley floor asks for. Re-baking that whole group every time a cutter
moves one cell would cost as much as baking the glacier from scratch, each
time.

### Options

| Option | Why not, or why |
|---|---|
| Bind the cutter's position to state, so each write rebuilds the document | A program rebuild per state write, and every group the cutter's program touches bakes whole again. Rejected |
| Rebake when the pose holds still (the settle rebake) | A retreat never holds still, so it never rebakes. Rejected |
| Skin the cut face to the cutter's slot | A hard cut has no share to blend by. The cut face would ride the cutter while its rim stays on the ice, so it stretches. Rejected |
| Splice re-contoured cells into the old mesh | The contour is not local: refinement budgets the whole group (`SdfSampledLattice.Refine`), planar regions grow across it (`SdfMeshPlanarMerge`) and collapse orders it globally (`SdfMeshCollapse`). Rejected |
| The cutter as an input of the surface's group, the group re-planned when the input moves (G1) | Correct and simple, and reuses the bake's existing re-plan of a moved input. On its own it re-bakes the whole group per step. Kept as the first step |
| G1 over source tiles, with each tile keyed by the cutter only where the cutter reaches it (G3) | Work follows the change: only the tiles the cutter's reach meets bake again. Recommended |

### The design

**A moving cutter becomes an operand of the surface's group.** An operand is a
cutter that rides its own motion owner's slot inside the group of the surface
it cuts. The group's field is the surface with the cutter where the cutter is
now, so a bake of the group is a correct bake of the carved surface. An operand
is not a skin member: no vertex rides it, and the group's mesh is drawn rigid
in the group's own frame. Every vertex of the cut face lies on the surface's
owner, so the cut face stays still while the cutter moves; only a new bake
moves it. As built (G1):

- `CreationMotion.OperandShapes` classifies an owner as an operand owner when
  every shape it carries cuts (`SdfBoundAlgebra.Carves`); the creation root
  never is. An operand owner still gets a slot. Its pairs neither skin nor
  crease (`CreationMovingBlend.Operand`), and PUCK122 reports the cutter as an
  operand. It is emitted inside the group that holds the surface, on its own
  slot; in a partitioned group every partition takes it there
  (`CreationMotionPlan.Borrows`), and an authored group left holding operands
  alone emits nothing (`CreationMotionPlan.HoldsSurface`). Relief still
  refuses it: that is G2.
- A group an operand cuts carries no skin. It bakes in the slot of its one
  surface owner (`CreationMotionPlan.TryCutSlot`), and a group holding more
  than one owner's surfaces beside an operand is partitioned by owner
  (`CreationMotion.SharedOperandGroups`), so a smooth seam there creases.
  `SdfMeshBakeGroup.Seal` refuses a group that reads an operand beside a skin
  member, the one door every planned group passes.
- The emitter declares each operand on the instance
  (`SdfInstanceRange.Operands`, one `SdfInstancePart` an operand shape: its box
  in its slot's frame), which needs no kernel change, since the bake kernel
  already samples any slot through the group's pose rows.
- `SdfMeshGeometryBuilder` is the one door. It hashes an operand slot's offset
  as each instruction is appended, and once the group's tier is known
  (`SdfMeshGeometryBuilder.AppendOperands`, called by the seal) its pose,
  relative to the group's own frame and snapped to the group's step, in the
  geometry and its form alike. It keeps that snapped pose with the poses the
  bake samples through. A rest-skin group (`RestPoseSkin`) freezes its member
  slots at the identity but never an operand slot.

The invariant: **a group's mesh is a function of its geometry, and an operand's
snapped pose is part of that geometry.** It is enforced in two places that
already exist. The geometry builder is the only place a pose enters the hash,
and the bake samples a group only through the pose rows its geometry wrote
(`SdfMeshBakeGroup.PoseRows`), never the frame's live transforms. A new caller
cannot get around either, because no other path feeds a pose to the bake
kernel. The guarantee stops at the step: a mesh shows the cut at the snapped
pose, up to one step behind the cutter.

**The step is the group's tier cell.** An operand's position snaps to the
group's tier cell (`SdfMeshTier.Cell` of the group's `Tier`). Its orientation
snaps to the angle that moves the farthest corner of the cutter's box by one
such cell about the cutter's own origin. A group drawn at its demanded tier
already carries an error under half a display pixel at the nearest demand
point (`SdfMeshTier.PixelShare`), so a swap between two steps moves the surface
by no more than the error the bake already accepts there. This is the seamless
guarantee, and it holds while the bake keeps up: while the cutter moves less
than one step in the time a re-carve takes to land. A cutter faster than that
lands its newest pose each time, a larger jump. The library's
`sdf.mesh-inputs` counters (`SdfMeshInputs`) count both cases: each operand
re-plan (`sdf.mesh-inputs.operand`), and each plan that replaced a waiting
successor (`sdf.mesh-inputs.coalesced`), so a reading shows when the bake falls
behind.

**Which changes re-bake.** The bake re-plans a group when a frame input its
geometry read has moved, through one door (`SdfMeshPathBake.Revisit`): a
sampled field's snapshot, and an operand's pose. It visits only the groups that
read one (`m_inputReaders`), keeps one bake in flight per group, and holds the
newest plan as the group's successor until that bake lands (`Promote`,
`Succeed`):

- Each frame, for each group that reads an operand, it snaps the operand's
  current relative pose at the group's step and compares it with the pose the
  geometry hashed (`SdfMeshGeometryBuilder.OperandsMatch`). The cost is one
  snap and compare an operand a frame, and none for any other group; a frame
  whose sampled set stands visits only these groups.
- A group whose operand moved a step is planned again
  (`SdfMeshBakeGroup.Refresh`) and placed through `Succeed`, or held as the
  successor of a bake still in flight, exactly as a resampled group is. A group
  whose successor already holds the frame's inputs is not planned again.

The same door can later admit the other inputs the geometry already hashes but
the bake never revisits: the lanes a lane erosion (`SdfOp.LaneErode`) or a
morph reads (`SdfMeshGeometryBuilder.AppendLanes`). That would let `erode` show
state live, which [sdf-authoring](../../.claude/skills/sdf-authoring/SKILL.md#binding-to-state)
says it cannot. It is listed as step G6, not built with G1.

**Work proportional to the change: source tiles and reach-clipped keys.** G1
re-bakes the whole group per step, and the glacier is one large group. The
unit that divides a large group already has a design:
[the source tile and its seam rule](streaming-and-memory.md#the-source-tile-and-its-seam-rule).
A placement declares a tile size, and the emitter writes one instance per tile,
the field cut by the tile's box grown by an overlap. Each tile is an ordinary
group and a closed surface whose closing face lies inside its neighbour, so
neighbours at different tiers or different steps overlap and never open a
hole. G3 builds that unit (it is not built yet; the streaming plan's step 10
then reuses it for terrain) for both emitters: the static stamper, as the
streaming plan describes, and the pooled root surface
(`WorldPlacementStamper.EmitRootSurface`), where each tile is a dynamic
instance that bakes separately on the root's slot. A tile holds only the shapes
whose box (`CreationStampEmitter.ShapeStampBox`), grown by their blend's reach
and the relief's amplitude, meets the tile's box.

A cutter that moves must stay in every tile it can reach, so its instructions
are in many tiles, and a plain pose key would re-bake all of them on every
step. The key is therefore clipped to the operand's reach:

- The operand's reach box is its box at the snapped pose, grown by its blend's
  growth (`SdfBoundAlgebra.Growth`) and smooth radius, the relief's amplitude
  (`CreationNoiseDocument.Amplitude`), and the distance at which a changed
  sample can still change the contour at the tile's tier: a fixed number of
  final cells and the gradient taps' spacing
  (`SdfMeshBakeRequest.TapSpacing`), which G3 derives from the contour's
  tests.
- The geometry hashes the operand's snapped pose only when that reach box
  meets the group's box. Otherwise it hashes "absent", so a tile the cutter
  does not reach keeps its key, and its mesh, as the cutter moves elsewhere.

The invariant: **in a tile the operand's reach misses, the field the bake reads
is the same at every pose the operand can take outside it.** For a
subtraction, the field changes only where the cutter is nearer than the
surface. Outside the cutter, signs never change. Inside the solid, a value can
rise toward zero, but only to minus the distance to the cutter, so a sample
farther than the reach keeps its sign and stays above every threshold the
contour reads by magnitude. This argument is not checked in code. G3's first
check measures it: bake one tile with the operand at two poses outside its
reach and compare the meshes. The guarantee stops at an intersection operand,
whose field changes everywhere outside it, so every tile is in its reach.

**How the swap stays clean.** A re-carved group is placed through the existing
replacement path. The old mesh keeps drawing while the new one bakes
(`SdfMeshPathBake.ShowReplaced`). The new mesh lands beside it in the mesh
region before any draw names it (`SdfWorldTables.NameMeshesAhead`), and the
draws move only once every piece stands (`SdfWorldTables.MeshStands`), with a
wave of replacements handing over together (`SdfMeshTier.HandFrames`). So no
frame draws a hole or a missing group. Between two tiles, one may show step n
while its neighbour shows step n+1. The overlap rule keeps that seam closed,
and the step between them is at most one step plus the two tiles' own errors,
which is the seam rule's own bound.

**Contact.** Contact never reads a baked mesh, so no swap disturbs it. It reads
the authored field (`CreationStampEmitter.EmitContact`), which today emits
every shape at its authored pose. The glacier is not solid, so it needs
nothing. A solid placement whose cutter moves (the door carved into rock)
collides with the rest cut today and still would after G1, which is silently
wrong. Step G5 closes that the way `sizeBy` already works. An operand whose pose
is a pure function of authority state (a `state.<row>` driver with a `linear`
or curve wave, and no `dynamics`, weight or blend time) is sampled by the
authority at the tick in fixed point, published in the tick's snapshot as the
solid's sampled size is (`WorldColliderSet.RefreshAttached`,
`WorldSnapshot.SolidScales`), and read by both contact and the draw. The drawn
cut then trails contact by at most one step plus the re-carve's landing time.
An operand driven by presentation (time, travel, a driver with dynamics) never
enters contact; on a solid placement PUCK122 says the cut collides at rest.

**Relief.** An operand owner composes one field per bake, so relief no longer
needs every shape to ride the root. G2 narrows the refusal in
`CreationMotionPlan` and the validator (PUCK030) to owners that are not operand
owners, and the relief then covers the cut face as it covers the rest of the
ice. Relief across a skinned or creased group stays refused, because each
partition or skin pose would draw a different relief at the seam.

**Determinism and cost.** Everything here is presentation: the cutter's pose
comes from a presentation driver and the meshes are caches keyed by their
inputs. The code change changes the bake identity once, through the bake's code
closure (`SdfMeshBakeClosure`). After that, a geometry's key moves only with the
operand's snapped pose, and no recorded hash or file is involved. The cost of a
change is:

- per frame, one snap and compare for each group that reads an operand;
- per step, a bake of each tile the operand's reach meets, at that tile's tier;
- at most one bake in flight and one successor per group, so a cutter that
  moves faster than its bakes land costs no more bakes, only a larger jump.

The bound: re-carves of a tile in a second are at most the cutter's speed over
the step, and never more than its bakes can land. For the glacier at
`water.speed 60` the front moves about 0.5 m a second against a 64 mm step,
about eight steps a second for each tile the front touches. Whether its tiles
land that fast is a measurement for the lead's quiet window (below).

### Relation to step 5: the settle rebake

The settle rebake waited for a skinned group's members to hold still away
from their baked pose, a rule wrong for an operand, which may never hold
still, and its watch was empty once every pooled skin baked at rest. G1
deleted it with its counters, its globally snapped poses, its one-slot plan
and the group state only it read. The replacement path (`StartReplacement`,
`FinishReplacements`) remains the tiers' alone, and the bake's ticket
transition is `SdfMeshPathBake.MoveTicket`. Step 5 keeps only its cost window.

### Steps, each with its check

- **G1. Operands, re-planned as frame inputs; the settle rebake deleted.**
  Built (the design above; the probe, validator and audit figures are in its
  commit). Its last check is a running World: `world.counters sdf.mesh-inputs`
  counts `sdf.mesh-inputs.operand` re-plans while a cutter moves (the gallery's
  `motion-carve`, or the probe placed in the debug world), and no
  `sdf.mesh-settle` source remains.
- **G2. Relief with operands.** Check: the probe with relief validates with
  no PUCK030, and `puck mesh preview` of it shows the relief on the cut face.
- **G3. Source tiles and reach-clipped keys**, for the static stamper and the
  pooled root surface. Checks: one tile baked with the operand at two poses
  outside its reach gives equal geometry keys and identical mesh bytes (the
  reach is widened until it does, and the number of cells it takes is
  recorded in the commit); `puck mesh audit` over every tile group of the
  glacier reads no open and no non-manifold edge, and neighbours overlap by
  the margin; with the cutter moving, the bake's start lines name only the
  tiles its reach meets.
- **G4. The proof exhibit: the glacier retreating by a cut.** `glacierMass`
  drops `sizeBy` and gains a retreat cutter, a rounded mass whose face is the
  retreat front, sliding up the trough by `state.glacierLength`, and the
  glacier's placement declares a tile size. The look is the authors': the
  front's shape, and whether a second operand thins the ice. Check (a
  verification request for the lead): with `water.speed 60`, presented bursts
  from the valley floor and from the lake shore show the toe wasting back up
  the trough with relief on the cut face, and no visible pop at the front;
  `world.counters` shows re-carves only on the tiles at the front.
- **G5. Contact for state-posed operands on solid placements** (with the
  physics skill). Check: a probe of a door cut into a solid block, its cutter
  driven by a state row; `body.where` walks through the opening after the
  state write, the sweep is refused at the old wall position, and
  `puck test --reproduce` agrees on two runs.
- **G6. Lanes as frame inputs.** A lane erosion and a morph re-plan their
  groups when the lanes they read move. Check: a part that erodes by a state
  lane shows its new erosion without a program rebuild.

G1, G2, G3 and G4 run in that order. G5 and G6 each need only G1. The lead's
quiet-window measurement for G4: at `water.speed 60`, the re-carve latency of
a front tile (start to hand-over) and the front's lag behind the cutter,
before and after G3, on Direct3D 12 and Vulkan.

## Scope it does not cover

Contact reads the authored field (`CreationStampEmitter.EmitContact`, the
field colliders), not the posed mesh. A pooled creation's seam is presentation.
A solid creation that needs posed contact needs its own design. Skinned meshes
keep no coarser levels (`SdfMeshCollapse.Levels`); a pose-aware level error is a
later step. A cutter that moves against its group's baked mesh is gap 8's
design, below the steps.

## Systems considered

The settle rebake, since deleted. The skin members
(`SdfMesh.Members`, `SdfMesh.VertexMembers`, the bake's skin round and
the former lifted skin sample). Snapped pose keys and form reuse (`SdfMeshGeometryBuilder.AppendOperands`,
`SdfMeshLibrary.TryTakeForm`). The pooled parts that bound each shape
(`SdfInstancePart`). The moving-blend warning (`CreationMotion.MovingBlends`)
and the swing bounds (`ShapeSwingDocument.MaxAmplitude`). Crease and material
copies (`SdfMeshMaterialCut`), planar merge (`SdfMeshPlanarMerge`), collapse
(`SdfMeshCollapse.Levels`) and the store codec (`SdfMeshStoreCodec`). The smooth
blend's factor (`blendSmoothUnion` in `field/sdf-blend.hlsli`) and the CPU
evaluator's render reading (`SdfFieldEvaluator`). The surface pass's layer
frame (`sdfMeshSurfaceNormal`, `sdfLayerFrame`). The decision that the frame
walks no field ([rendering decisions](../decisions/rendering.md)).
`experimental/` holds no skinned-seam or pose-bank prior art. A history search
for "skin", "settle" and "seam" found the settle rebake and the skin members as
the only prior work.

The duplicate scan (`puck scan --only duplicates --against lead/velocity`) was read
in full: 81 neighbours and four helper flags on the final implementation. Actual
overlap was removed: the CPU contour sampler moved from the CLI into `SdfMeshCpuField`,
and pair packing moved into Maths' `VectorFunctions.PackHalf2x16`, now shared by skin,
irradiance and contact appearance. Searches and their relevant results:

```text
rg -n 'HalfToUInt16Bits|PackHalf2x16' src/Puck.Maths src/Puck.SdfVm/SdfMeshInfluence.cs src/Puck.SdfVm/SdfMeshIrradiance.cs src/Puck.SignedDistance/SdfAppearance.cs
VectorFunctions.PackHalf2x16; SdfMeshInfluence.GradientXY; five irradiance pairs; contact reach/coverage pair
rg -n 'FixedRigidTransform|ContourLevel|MemberFields' src/Puck.SdfVm -g 'SdfMeshSkin*.cs'
SdfMeshSkin.Pose: FromRotationTranslation, FromDualQuaternion
SdfMeshSkin.Split: SdfMeshAudit.MemberFields, SdfMeshPathBake.ContourLevel
```

The CPU pose witness therefore reuses the existing dual-quaternion carrier, while
the shader supplies its presentation-float execution. The new envelope composes
existing motion bounds and shape boxes; it is not another animation evaluator.
Influences are an optional reading of the existing point interpreter, not a new
field walker. The scanner's presentation hash helpers already call `Fnv1aHash`;
their per-type `Add` overloads serialize different record layouts. Irradiance's
`RefreshLighting` uses BCL `HashCode.Combine` for a local cache key. Neither is a
private hash algorithm. Other neighbours compare unrelated record/cell layouts,
or the two interfaces' sampling attribution wrappers, rather than duplicate kernels.

G1 built no new re-plan mechanism. The operand's re-plan is the sampled field's
door widened (`SdfMeshPathBake.Revisit`, `Promote`, `Succeed`,
`SdfMeshBakeGroup.Refresh`); its snap is the geometry's existing relative pose
and rounding (`SdfMeshGeometryBuilder.RelativePose` and its rounded pose
record), used at the tier's cell; its declaration reuses the instance part
(`SdfInstancePart`) rather than a new type or an instruction flag; and its
classification reuses `SdfBoundAlgebra.Carves` and the motion owners
(`CreationRig.Owner`), keyed so the validator and the pooled plan share one
rule (`CreationMotion.OperandShapes`, `SharedOperandGroups`). The replacement
path the settle rebake shared with the tiers stays the tiers'.
