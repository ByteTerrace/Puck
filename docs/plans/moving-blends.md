# Blends between parts that move

A smooth blend between two parts of an animated creation that move against each
other (the swallow's head glancing on its body) tears in motion. This memo sets
out how such a seam can hold under continuous motion, and recommends one
approach. Nothing here is built yet: the design is reviewed before code.

## What happens today

- A pooled blend group bakes one mesh at the pose its program arrives in
  (`SdfMeshBakeGroup.Plan`, `SdfMeshGeometryBuilder.Poses`). A second points
  round over the finished mesh records each vertex's winning slot, so the mesh
  keeps each other slot as a member (`SdfMesh.Members`) and each vertex as one
  member's (`SdfMesh.VertexMembers`). The vertex stage carries a member's vertex
  through that slot's live transform (`sdfMeshWorldPosition` in
  `frame/sdf-mesh.hlsli`).
- Every vertex follows exactly one slot. Across a blend seam, neighbouring
  vertices belong to different slots, so the triangles between them stretch as
  the parts separate. A glance tears the throat into spikes.
- The settle rebake (`SdfMeshPathBake.Settle`, `SdfMeshSettle`) bakes the group
  again once the relative pose holds still, at snapped poses whose meshes the
  library shares. That fixes a pose that holds, but not continuous idle motion,
  which never settles.
- PUCK122 (`CreationMotion.MovingBlends`) warns the author when this happens.

## The invariant any answer keeps

Every view draws baked meshes, with no field walk in the frame. The bake is the
only GPU kernel that walks the field, and it has one `mapCore` call site (the
[rendering skill](../../.claude/skills/rendering/SKILL.md#editing-kernels)). A
mesh is a function of its geometry (`SdfMeshGeometryBuilder`). A seam answer
that walks the field per pixel or per vertex breaks the first two rules. One
that bakes per frame breaks the bake's latency and budget model.

## Options

1. **Settle rebake alone (what exists).** No new cost. A seam is right only
   where the pose holds. A time-driven idle never holds, so it stays torn.
   Rejected as the answer for continuous motion, but kept as the correction
   pass for poses that do hold.
2. **A bank of baked poses.** Bake the group at N snapped poses across a
   driver's range (a glance's sine is one-dimensional) and draw the nearest.
   The library already keys snapped poses. Bake work and resident bytes grow N
   times for each moving joint, and they multiply across joints. Swapping meshes
   pops, and independent contours share no vertices to interpolate between.
   Rejected: the cost grows with the pose space, and the result is never
   continuous.
3. **A skirt per part.** Each moving part bakes alone and rigid, exactly as an
   ungrouped part does, with an overlap skirt reaching into its neighbour so no
   gap opens. This holds without tearing, but it draws a crease, not a blend:
   two interpenetrating meshes meet along the depth test's intersection line.
   It matches what grouping each moving part on its own already gives (the
   sdf-authoring skill's advice), so it adds nothing new.
4. **A blend zone evaluated in the vertex or pixel stage** from both parts'
   fields. This puts a field walk in the frame. It breaks the one-call-site rule
   and the "no field walk in the frame" decision
   ([rendering decisions](../decisions/rendering.md)), and it costs register
   pressure in every mesh draw. Rejected.
5. **Continuous seam rebakes.** Rebake only the seam's box, every few frames,
   at low resolution. This needs no new representation. But each bake is
   several frames of GPU dispatches, readback round trips, CPU contour and
   landing (`SdfGpuWork`, `SdfMeshPathBake`), so the seam lags the motion and
   pops when each new mesh lands. Its cost scales with every moving body on
   screen. Rejected as the baseline. It could later be an upper-hardware
   enhancement, but only if it measures as paying.
6. **A skinned seam: two members and a weight a vertex (recommended).** The
   bake already answers each vertex's winning slot in its skin round. Have it
   answer the two slots whose shapes compose at that vertex, and the blend's
   share between them: the smooth union's own interpolation factor `h` where
   `mapCore` composes them. The vertex stage then carries the vertex through
   both slots' transforms, weighted by `h`, using dual quaternions so a twist
   keeps its volume. A vertex outside any blend has weight 1 on one slot and
   draws exactly as it does today.

## Why the skinned seam

- It is continuous: a vertex's position is a smooth function of the two poses,
  so nothing tears and nothing pops, whatever the motion.
- It keeps every invariant. The weight comes from the bake, through the one
  `mapCore` call site, and is stored with the mesh, so the mesh is still a
  function of its geometry. The frame still walks no field.
- It costs a second member index and a weight per vertex in the mesh and the
  region (a few bytes beside the six floats), plus one more transform fetch in
  the two raster vertex stages and the surface pass's reprojection. Bake work
  does not change: the skin round already samples every vertex.
- It is how games hold a neck or a shoulder: blend skinning at a joint, with
  the weights authored here by the field's own blend.
- The settle rebake stays. Once a pose holds, the group bakes again there, which
  removes the residual error of skinning a seam far from its baked pose.

Where the guarantee stops: skinning approximates the field's blend. Error grows
with the relative turn from the baked pose. Past roughly 60 degrees the blended
region thins or bulges where the true field would not. The settle rebake and
the pose the program arrives in bound how far that drift reaches. PUCK122 then
changes from "the seam stretches" to "the seam deforms, and is exact again once
the pose settles".

## Steps, each with the check that shows it worked

1. **The bake answers two slots and a weight.** The skin round in
   `passes/sdf-mesh-bake.comp.hlsl` writes the second slot and `h` beside the
   winning slot. `SdfMesh` carries `VertexMembers2` and `VertexWeights`, and the
   store codec persists them. Check: `puck mesh audit --cpu` over the swallow
   prints the vertices whose weight lies strictly between 0 and 1, and they lie
   only within the blend's `smooth` radius of the seam. A CPU law compares
   weights against `SdfFieldEvaluator`'s blend factor at the same points.
2. **The vertex stage blends.** `sdfMeshWorldPosition`, `sdfMeshWorldNormal` and
   `sdfMeshPreviousPosition` blend the two members' transforms by dual
   quaternion. Check: the swallow, booted windowed on both backends, captured in
   a presented burst through a full glance (`puck_capture_frame` with
   `mode: "presented"`, 60 frames), shows no spikes at the throat. The static
   `motion` view stays zero on still parts.
3. **The audit measures a posed skin.** `puck mesh audit` gains a pose argument
   that measures the skinned mesh at a pose against the field at that pose.
   Check: at the glance's extremes, the off-surface p99 at the seam stays within
   the bake cell, where today's one-member skin measures the tear.
4. **PUCK122's wording follows.** The diagnostic and the sdf-authoring skill's
   "Making it move" section describe a deforming seam. Check: `puck compile
   --validate` on the swallow prints the new text.

## Systems considered

The settle rebake (`SdfMeshPathBake.Settle`, `SdfMeshSettle`). The skin members
(`SdfMesh.Members`, `SdfMesh.VertexMembers`, the bake's skin round). Snapped pose
keys and form reuse (`SdfMeshGeometryBuilder.Snap`, `SdfMeshLibrary.TryTakeForm`).
The moving-blend warning (`CreationMotion.MovingBlends`). The decision that the
frame walks no field ([rendering decisions](../decisions/rendering.md)). The
background bake budget (`SdfGpuWork`). `experimental/` holds no skinned-seam or
pose-bank prior art; a history search for "skin", "settle" and "seam" found the
settle rebake and the skin members as the only prior work.
