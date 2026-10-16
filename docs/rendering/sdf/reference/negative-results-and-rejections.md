# Rejected and conditional SDF techniques

This page records current non-goals and the evidence required to reconsider
them. It is not a chronology and it is not a work list—nothing tracks open
SDF implementation work today.

## Global voxel representation

**Current decision:** Not a replacement for the analytic SDF program.

A global voxel field or clipmap changes the content model, introduces update
and residency policy, and weakens exact analytic detail. `SampledRegion` already
provides a bounded cache for dense carve sets while leaving the analytic stream
authoritative.

**Reconsider when:** A distinct content source requires large volumetric data
that cannot be represented or cached as bounded regions.

## General BVH or hardware ray-tracing hierarchies

**Current decision:** Not applicable to the frame.

The frame marches no ray: every view rasterizes baked meshes, and the bake
selects a group's instances with a mask. A hierarchy over analytic instances
would serve only a renderer that traces the field per pixel.

**Reconsider when:** A view needs per-pixel field rays again.

## Per-tile instruction pruning and march scheduling

**Current decision:** Not applicable.

Tile-pruned instruction tapes and wavefront or persistent-thread march
schedulers reduce the cost of per-pixel field marches, which no view takes.
The bake's cost is bounded per frame by `SdfMeshPathBake.FrameTaps` instead.

## Coverage rasterizers as geometry

**Current decision:** Rejected.

Coverage-from-outline systems produce excellent antialiased text, but coverage
is not a conservative distance field. Use such output in the decal or overlay
tier, not as marchable geometry.

## Negative authored scale as a mirroring spelling

**Current decision:** Rejected. A negative per-axis scale on a creation shape
is refused at the creation document validator.

Mirroring already has a spelling: the `SymmetryPlane` domain op, authored as a
`symmetry` entry in a shape's domain list, which reflects across an arbitrary
plane, is an exact isometry, and expands to rigid copies so contact matches
render: each mirrored copy is a proper pose followed by
`SdfProgramBuilder.MirrorX`, so a shape with no symmetry of its own, such as a
convex prism, still mirrors exactly. `MirrorX` is the program's one mirror
spelling, an existing scale op of (-1, 1, 1) that leaves distance unscaled, and a
mirrored placement uses it too, reflecting the point once after the stamp's
scale. A sign on a scale component would be a second spelling of the same
mechanism, and a strictly weaker one—it can only mirror across the shape's
own axis planes.

It also does not mirror anything today. Every emission path reads a scale's
magnitude (`SdfSolidGeometry.AppendScaledPrimitive`,
`CreationStampEmitter.EffectiveScale`), so the sign changes no geometry; its
only observable effect was that reach disagreed with the emitted surface, and
because reach folds into a running maximum seeded at zero, the placement
shipped a cull bound of nothing but its margin around geometry that was still
there. `SdfSolidGeometry.Reach` reads magnitudes for the same reason its
emission sibling does, so the two describe one object whatever the caller
hands them.

**Reconsider when:** A scale sign is given a meaning emission actually
implements, and that meaning is something the symmetry domain op cannot
express.

## Runtime chamfer distance transform

**Current decision:** Rejected.

Chamfer masks introduce directional distance error and require an extra safety
penalty. Coverage-to-distance conversion is not a supported replacement for
the managed outline generator: coverage is appropriate for decals, while
marchable glyphs require outline-derived fields with documented safety limits.

## Unbounded procedural displacement

**Current decision:** Rejected.

Noise without a range and derivative bound cannot produce a safe step scale or
instance bound. Procedural detail is acceptable only when its hash,
amplitude, derivative, and deterministic replay behavior are explicit —
`NoiseDisplace` is the admitted form (see
[Lipschitz and field correctness](lipschitz-and-field-correctness.md)); this rejection
continues to bar any unbounded basis.

## Backend-specific scheduling or shader features

**Current decision:** Rejected for the shared render contract.

Puck supports Vulkan and Direct3D 12 as equivalent backends. A feature may use
different low-level mechanisms only when both implementations preserve the
same document, shader, and observable behavior contracts and retain a portable
reference path.
