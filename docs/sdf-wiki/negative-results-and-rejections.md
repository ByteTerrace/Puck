# Rejected and conditional SDF techniques

This page records current non-goals and the evidence required to reconsider
them. It is not a chronology and it is not a work list — nothing tracks open
SDF implementation work today.

## Global voxel representation

**Current decision:** Not a replacement for the analytic SDF program.

A global voxel field or clipmap changes the content model, introduces update
and residency policy, and weakens exact analytic detail. `SampledRegion` already
provides a bounded cache for dense carve sets while leaving the analytic stream
authoritative.

**Reconsider when:** A distinct content source requires large volumetric data
that cannot be represented or cached as bounded regions.

## General BVH or hardware ray-tracing hierarchy

**Current decision:** Conditional.

The uniform grid and beam masks match current analytic-instance workloads with
simple deterministic packing. A BVH or TLAS/BLAS split adds rebuild policy and
backend-specific traversal concerns.

**Reconsider when:** Profiles show density skew or sparse-world scale causing
the uniform grid to dominate on both backends.

## Per-tile instruction-tape pruning

**Current decision:** Rejected for ordinary flat programs; conditional for
large multi-segment creations.

Specialization metadata and dispatch cost can exceed the interpreter work it
removes. Ordered point and field state also make many ranges unsafe to omit.

**Reconsider when:** Real creations contain enough independently bounded
segments that most tiles evaluate only a small fraction of the stream.

## Wavefront and persistent-thread marching

**Current decision:** Conditional.

These schedules can improve utilization for highly divergent rays but require
queues, compaction, overflow handling, and portable synchronization semantics.

**Reconsider when:** Lane-utilization profiles identify march divergence as the
dominant cost after field and shading work are accounted for.

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
render. A sign on a scale component would be a second spelling of the same
mechanism, and a strictly weaker one — it can only mirror across the shape's
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
penalty. The deterministic exact Euclidean distance transform used by
`SdfCoverageAtlas` is the fallback generator.

## Unbounded procedural displacement

**Current decision:** Rejected.

Noise without a range and derivative bound cannot produce a safe step scale or
instance bound. Procedural detail is acceptable only when its hash,
amplitude, derivative, and deterministic replay behavior are explicit.

## Backend-specific scheduling or shader features

**Current decision:** Rejected for the shared render contract.

Puck supports Vulkan and Direct3D 12 as equivalent backends. A feature may use
different low-level mechanisms only when both implementations preserve the
same document, shader, and observable behavior contracts and retain a portable
reference path.
