// The hit passes' common head. Primary, surface, ambient, shadow, the mesh surface and views each define their pass
// macro, include this head, then their own stage, then the entry point (sdf-hit-entry.hlsli), so a hit kernel's include
// set is the world modules (sdf-world.hlsli) and the stage it compiles: an edit to another pass's stage recompiles
// nothing here. Every hit pass reads its resources through the sdf-world interface: dynamic transforms, screen sources,
// and the read-only instance mask instance-cull produced (sdfInstanceMasks). Primary, surface, ambient and shadow write
// the visibility records through sdfVisibilityRecordsRW, and views reads them through sdfVisibilityRecords
// (sdf-visibility.hlsli).
#ifndef PASSES_SDF_HIT_PASS_HLSLI
#define PASSES_SDF_HIT_PASS_HLSLI
// Unused shading resources compile out of primary traversal.
#define SDF_DYNAMIC_TRANSFORMS
#ifndef SDF_PRIMARY_PASS
#define SDF_PRIMARY_READ
#if !defined(SDF_SURFACE_PASS) && !defined(SDF_AMBIENT_PASS) && !defined(SDF_SHADOW_PASS) && !defined(SDF_MESH_SURFACE_PASS)
#define SDF_VIEWS_PASS
#endif
#endif
#define SDF_FRAME_INSTANCE_GRID
#define SDF_INSTANCE_MASKS
#define SDF_SEGMENT_TAPES
#define SDF_SCREEN_SOURCES
// Primary and views sample the glyph atlas, so primary marches true lettering.
// The beam and other non-atlas kernels retain the conservative cell box.
#define SDF_GLYPH_ATLAS
// The brick pool: primary and shading sample baked SampledRegion carves O(1), so the primary, shadow and occlusion marches
// do not pay for every carve. All views variants inherit it.
#define SDF_SAMPLED_REGIONS
// The bounded volumes are in the shared interface, but only the composite pass integrates them (shade-volumes.hlsli).
// The per-group ambient and shadow gathers (surface/sdf-shadow-gather.hlsli): one groupshared candidate mask per 8x8
// workgroup, built cooperatively at a uniform seam of the ambient and shadow stages. Every lane, rendered pixel or not,
// reaches its stage, so the entry point turns the per-pixel extent test into the pixel's `active` flag instead of a
// return. Primary, surface and views gather nothing and hold no groupshared mask.
#if defined(SDF_AMBIENT_PASS) || defined(SDF_SHADOW_PASS)
#define SDF_GROUP_SHADOW_GATHER
#endif
// The soft-shadow march strides on the bounds its walk rejects by (field/sdf-map.hlsli).
#ifdef SDF_SHADOW_PASS
#define SDF_QUERY_REJECTION_BOUND
#endif
// One shadow kernel and one kernel per views variant serve every fade capacity: each compiles both fade
// slots and reads the active fade count from the pass block, which a graph without the incoming image writes as zero.
#if defined(SDF_SHADOW_PASS) || defined(SDF_VIEWS_PASS)
#define SDF_SHADOW_FADE_SLOTS 2
#endif
#define SDF_PART_RAY_BOUNDS
// Every hit pass reads the beam's tile planes and part bounds through tiles, and the surviving-tile box from the cull-args
// pass through cullBounds (sdf-cull-args.comp): its group origin, then its exclusive group end. The dispatch is
// origin-anchored, so the origin offsets each invocation onto the box's pixels, and the all-empty margins outside the box
// are never dispatched; the whole box is where this frame wrote visibility records, which worldVisibilityCurrent reads.
#include "sdf-world.hlsli"
#include "../march/sdf-part-bounds.hlsli"
#include "../frame/sdf-mesh-impostor-surface.hlsli"
#include "../march/sdf-pixel.hlsli"

#endif
