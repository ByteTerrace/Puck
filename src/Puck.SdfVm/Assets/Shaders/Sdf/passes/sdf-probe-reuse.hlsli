// The test that decides when one irradiance gather (sdfMeshProbesAt) may serve another receiver, shared by every reader
// that reuses gathers: the probe lookup's cells and history (sdf-views-probes.hlsli, sdf-probe-history.hlsli) and the
// glass pass's history (sdf-glass-stage.hlsli).
//
// What a reused gather may differ in: the trilinear shares, facing fade and surface visibility of the receiver it was
// taken at, which lies within the reach of the receiver it serves, on the same tangent plane (both sdfViewsProbeTolerance)
// and facing within SdfViewsProbeFacing of it. The shares move at the probes' spacing and the facing fade at half of it,
// both metres or more; the surface test closes over SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE across a surface a probe
// measured, and a receiver that stays on its own plane does not cross the plane of a surface beside it.
#ifndef PASSES_SDF_PROBE_REUSE_HLSLI
#define PASSES_SDF_PROBE_REUSE_HLSLI
#include "../shade/sdf-mesh-irradiance.hlsli"

// The reuse test's tolerances (see the head of this file): the distance a reused receiver may lie from the pixel's, which
// is the normal bias every receiver is already pushed by; its distance off the representative's tangent plane, a quarter
// of the surface test's closing ramp; and the cosine its normal keeps with the pixel's.
static const float SdfViewsProbeReach = SDF_MESH_IRRADIANCE_NORMAL_BIAS;
static const float SdfViewsProbePlane = (0.25 * SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE);
static const float SdfViewsProbeFacing = 0.98;
// The least cosine a pixel's footprint is stretched by along the surface it meets, so a surface seen edge-on shares no
// farther than eight footprints.
static const float SdfViewsProbeGrazing = 0.125;

// A gather's measured flags beside its share (sdfViewsProbesFlags).
static const uint SdfViewsProbeMeasured = (1u << 16u);
static const uint SdfViewsProbeRemainderMeasured = (1u << 17u);

// The tolerances a reused gather is held to at a receiver whose pixel spans `footprint` metres across its ray, met at
// `cosine` (the absolute cosine between the ray and the receiver's geometric normal). In `x`, the reach it may lie from
// the receiver: the normal bias, or a cell and a half measured along the surface where that is wider, since the
// representatives a pixel blends stand a cell apart about it and the preceding frame's stood anywhere in their cells,
// and a pixel met at a grazing angle spans its footprint over the cosine along the surface (within SdfViewsProbeGrazing).
// In `y`, its distance off the representative's tangent plane: SdfViewsProbePlane, or half a pixel where that is wider.
// Where that reach spans more than the bias, a kept gather serves a receiver at the lookup's own resolution on the
// screen, and the plane and facing tests still keep a receiver from reading a gather across a crease or a depth edge.
float2 sdfViewsProbeTolerance(float footprint, float cosine) {
    return float2(max(SdfViewsProbeReach, (((1.5 * float(SDF_VIEWS_PROBE_RATE)) * footprint) / max(cosine, SdfViewsProbeGrazing))), max(SdfViewsProbePlane, (0.5 * footprint)));
}
// Whether a receiver at `offset` from a representative whose normal is `representativeNormal`, with its own geometric
// normal `normal`, may read the representative's gather (the head of this file): within the reach of `tolerance`
// (sdfViewsProbeTolerance), on its tangent plane within the tolerance's plane distance, and facing as it does. The probe
// lookup decides with it which pixels of a cell gather for themselves, and every history which receivers reuse a
// preceding render's gather (sdf-probe-history.hlsli).
bool sdfViewsProbesCompatible(float3 offset, float3 representativeNormal, float3 normal, float2 tolerance) {
    return ((dot(offset, offset) <= (tolerance.x * tolerance.x))
        && (abs(dot(offset, representativeNormal)) <= tolerance.y)
        && (dot(normal, representativeNormal) >= SdfViewsProbeFacing));
}
// The cells a view's records cover along each axis: the view's 8x8 tiles at SDF_VISIBILITY_GROUP_EDGE /
// SDF_VIEWS_PROBE_RATE cells a side, as SdfWorldPackage allocates them per tile of the extent.
uint2 sdfViewsProbeCells() {
    uint2 tiles = ((passGroup.imageExtent + (SDF_VISIBILITY_GROUP_EDGE - 1u)) / SDF_VISIBILITY_GROUP_EDGE);

    return (tiles * (SDF_VISIBILITY_GROUP_EDGE / SDF_VIEWS_PROBE_RATE));
}
// A gather's share and measured flags, as the state word and an extra slot's last word hold them.
uint sdfViewsProbesFlags(SdfMeshProbes probes) {
    return ((uint)round(saturate(probes.share) * 65535.0)
        | (probes.measured ? SdfViewsProbeMeasured : 0u) | (probes.remainderMeasured ? SdfViewsProbeRemainderMeasured : 0u));
}

#endif
