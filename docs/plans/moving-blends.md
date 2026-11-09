# Blends between parts that move

A smooth blend between two parts of an animated creation that move against each
other (the swallow's head glancing on its body) tears in motion. This memo sets
out how such a seam holds under continuous motion and recommends one design,
reviewed before code. The Moth's rebuild relies on whatever lands here, so each
step names its owner type and the check that shows it worked.

## What happens today

- A pooled blend group bakes one mesh at the pose its program arrives in
  (`SdfMeshBakeGroup.Plan`, `SdfMeshGeometryBuilder.Poses`). A second points
  round over the finished mesh (`SdfMeshPathBake`, the `skinned` branch of
  `Build`) samples each vertex lifted `SkinLift` (0.5 mm) along its normal and
  records the winning slot. The mesh keeps each other slot as a member
  (`SdfMesh.Members`) and each vertex as one member's (`SdfMesh.VertexMembers`).
  The vertex stage carries a member's vertex through that slot's live transform
  (`sdfMeshWorldPosition` in `frame/sdf-mesh.hlsli`).
- Every vertex follows exactly one slot. Across a blend seam, neighbouring
  vertices belong to different slots, so the triangles between them stretch as
  the parts separate. A glance tears the throat into spikes.
- The settle rebake (`SdfMeshPathBake.Settle`, `SdfMeshSettle`) bakes the group
  again once the relative pose holds still, replacing the mesh when it lands.
  A form baked at another pose can also be reused (`SdfMeshLibrary.TryTakeForm`),
  so a skin's reference pose is whatever pose first baked it.
- PUCK122 (`CreationMotion.MovingBlends`) warns that the seam stretches. It
  compares motion owners only; it bounds no motion.

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

1. **Settle rebake alone (what exists).** A time-driven idle never holds, so it
   stays torn. Rejected; it is retired by this design (below).
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
pop are deleted (`SdfMeshPathBake.Settle`, `SdfMeshSettle`, the
`sdf.mesh-settle` counters) once no group needs them: every moving blend is
either inside the envelope (skinned) or split (rigid). This narrows the
continuity guarantee honestly. Within one baked mesh, motion is continuous. A
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

## Steps, each with the check that shows it worked

1. **Rest-pose bake and the envelope.** `SdfMeshGeometryBuilder` keys a skinned
   group at rest. `CreationMotion` computes θ and D per moving blend, and
   PUCK122 reports the outcome. The pooled emitter splits a blend past its
   envelope. Checks: on the draft swallow, `puck compile --validate` prints θ,
   D and the split decision for each pair. `puck mesh audit --cpu` keys the
   same geometry at any arrival pose. A law shows rest is the identity delta for
   a swing, a slide and a frame.
2. **Influences.** The evaluator's influence reading, the per-place skin round,
   the two-owner truncation and the split on a third owner. `SdfMesh` gains the
   influence arrays, and planar merge and the codec carry them. Checks: the
   audit prints blend-zone vertices and finds zero places with split tuples. A
   law compares the propagated shares with leaf shares for nested smooth unions
   (the ¼, ¼, ½ case). A planar-merge law keeps every blend-zone vertex and
   every tuple.
3. **Vertex stage.** Dual-quaternion blending in the group's frame with the
   hemisphere rule, and the normal correction, in `sdfMeshWorldPosition`,
   `sdfMeshWorldNormal`, `sdfMeshPreviousPosition` and the shadow stage. Checks:
   a presented burst on both backends through a full admitted glance (60
   frames) shows no spike and no fold at the seam. The `motion` debug view stays
   zero on still parts. `puck mesh audit` with a pose argument measures the
   posed skin against the posed field within D at the envelope's extremes.
4. **Material coordinates.** The rest-point mapping for paints, insets and
   weathering, and blended lanes, in the opaque and glass passes. Check: a
   striped palette across a moving neck, captured in a presented burst, shows
   stripes fixed to the surface.
5. **Settle retired and cost measured.** Delete the settle rebake and its
   counters once steps 1 to 3 hold. Then run the quiet window above. Checks:
   `world.counters sdf.mesh-settle` no longer exists, and the frame bar holds in
   the close-up and the crowd on both backends.

## Scope it does not cover

Contact reads the authored field (`CreationStampEmitter.EmitContact`, the
field colliders), not the posed mesh. A pooled creation's seam is presentation.
A solid creation that needs posed contact needs its own design. Skinned meshes
keep no coarser levels (`SdfMeshCollapse.Levels`); a pose-aware level error is a
later step.

## Review findings and how each is answered

| Finding | Decision |
|---|---|
| F1 fixed weights do not bound separation | Admitted envelope θ ≤ 60°, D ≤ k/2, computed at compile time. Past it the blend splits into a crease and PUCK122 states it. The Jacobian determinant stays ≥ ½. |
| F2 dual-quaternion branch | Blend only group-frame relative motions. Hemisphere of the first owner, which never switches inside the envelope. Normalization stays ≥ 0.5. The same rule applies to previous positions. |
| F3 normals miss the weight derivative | Store ∇w and correct the normal by the inverse-transpose Jacobian, bounded by the envelope. |
| F4 lifted samples split crease copies | One tuple per place, sampled at the exact position and shared by every copy. The winning member moves to the same rule. Audited at zero. |
| F5 a final h is not the accumulated share | Influence vectors propagated through every composition and scope on the CPU evaluator. Blends with no share are admitted only within one owner. Two owners a vertex, with a split past a 1/64 third share. |
| F6 post-passes drop the attributes | Planar merge compares the whole tuple and keeps blend-zone vertices. `Build` remaps the arrays. The codec persists them. Collapse and chunks are already excluded. |
| F7 paints switch rigid frames | Rest point from interpolated rest positions, mapped through the layer parent's baked pose. Lanes blend by share. The rule covers opaque and glass. |
| F8 settle rebake pops | Bake at the authored rest. The envelope bounds error at every pose, so the settle rebake is deleted, and the guarantee is stated per baked mesh. |
| F9 per-pixel cost omitted | Counted model, a rigid fast path, a per-triangle resolve helper and a packed 12-byte layout. A quiet-window measurement of close-up and crowd on both backends is owned by step 5. |
| Backend identity | Influences are bitwise identical for identical meshes and the same function everywhere. Posed output is visually equivalent across backends, since the posed blend is presentation float math. |

## Systems considered

The settle rebake (`SdfMeshPathBake.Settle`, `SdfMeshSettle`). The skin members
(`SdfMesh.Members`, `SdfMesh.VertexMembers`, the bake's skin round and
`SkinLift`). Snapped pose keys and form reuse (`SdfMeshGeometryBuilder.Snap`,
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
