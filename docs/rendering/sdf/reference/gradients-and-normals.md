# Gradients and normals

A view's normal is the baked mesh's: the surface pass interpolates the mesh's
vertex normals at the hit, carried to world space by the draw's
inverse-transpose normal matrix, or takes the face normal where the mesh
carries none. The mesh's vertex normals come from the bake's gradients, which
the dual contouring reads at each cell's corners.

## The bake's gradients

The bake measures a gradient only at the corners of cells whose corners
disagree in sign and at the crossings it places vertices by: the samples
`SdfDualContouring` reads a normal at. A cell's normal reads its own eight
corners, so no other sample needs one.

A gradient lattice runs its own kernel, `sdf-mesh-bake-gradient`, which walks
the field once in forward mode (`SDF_FIELD_DUAL`, `field/sdf-dual.hlsli`):
every point is carried with its Jacobian over the world point and every
distance with its gradient, so the walk returns the value and its analytic
gradient together. The same walk source serves both bake kernels. Each shared
formula is generated a fourth time as an HLSL overload over duals
(`SdfFormula.EmitAllDual`), so a shape's, blend's or point map's arm resolves to
its dual by the types it is handed; the hand-written warps carry the Jacobian
themselves; a hard minimum, a clamp or a select takes its active branch's
derivative, so a crease reads one face's gradient where a stencil would blend
both. An arm that keeps no derivatives (`SdfShaderAction.Dual`) marks the
sample uncovered, and only that sample takes four tetrahedral taps around it
(`SdfMeshBakeTaps`), through the same call site. The bake's start line names
the program's ops that do so (`stencil-ops=[…]`). A primitive the formulas do
not carry (a polygon, a path, a glyph, a sampled region, a sweep) takes four
taps of itself alone, never of the program. A gradient sample counts
`SdfMeshBakeRequest.GradientCost` toward the bake's batch and lattice bounds
(`SdfMeshPathBake.BatchTaps`, `LatticeTaps`). A dynamic group's sample is
carried through its slot's transform, and its gradient comes back along the
group's own axes.

`SdfFieldDeviceLawTests.Gradients` holds the dual gradient to the stencil on
both backends wherever no crease passes through the taps.

## Failure modes

- A domain transform changes the gradient basis as well as the sample point.
- Non-uniform scaling requires the inverse-transpose relationship, which the
  draw's normal matrix carries.
- Repetition and fold boundaries are intentionally non-differentiable. Do not
  infer a smooth normal across them; the contour splits a cell there.
- Smooth field blends and material blends are related but distinct operations.
- Sampled regions need a gradient rule consistent with their trilinear field.

Validate normal changes with `world.debug-view normals`, hard and smooth
blends, transformed primitives, and captures on both backends.
