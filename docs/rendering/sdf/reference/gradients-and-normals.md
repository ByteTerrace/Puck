# Gradients and normals

A view's normal is the baked mesh's: the surface pass interpolates the mesh's
vertex normals at the hit, carried to world space by the draw's
inverse-transpose normal matrix, or takes the face normal where the mesh
carries none. The mesh's vertex normals come from the bake's gradients, which
the dual contouring reads at each cell's corners.

## The bake's gradients

The bake measures a gradient with four tetrahedral taps of `mapCore` around a
sample, at a spacing tied to the lattice's own cell, and only at the corners of
cells whose corners disagree in sign: the samples `SdfDualContouring` reads a
normal at. A cell's normal reads its own eight corners, so no other sample needs
one. A gradient sample costs five field evaluations, which the bake's frame
budget (`SdfMeshPathBake.FrameTaps`) counts as five. A dynamic group's taps are
carried through its slot's transform with the sample, so its gradient comes
back in the group's own frame.

`gpu.shapes.evaluated` counts every tap; `gpu.shapes.gradients` counts analytic
primitive derivatives, which no kernel takes.

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
