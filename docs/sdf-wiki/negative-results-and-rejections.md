# Rejected and conditional SDF techniques

This page records current non-goals and the evidence required to reconsider
them. It is not a chronology. Open implementation work belongs in
[the SDF backlog](../sdf-backlog.md).

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
