// The receiver's approach: complete-field samples along its camera ray, short of the surface, whose clear ball joins
// the surface sample's own. The receiver pass marches it (sdf-indirect-receiver.hlsli) before a shared proof can read a
// canonical record; a receiver with no joined sample launches along its normal under the shared admission instead.
#ifndef SDF_INDIRECT_APPROACH_HLSLI
#define SDF_INDIRECT_APPROACH_HLSLI
// The approach starts this fraction of the finest spacing back along the camera ray and takes at most this many
// samples toward the surface, the surface sample included. A sample farther than half a spacing is never a launch.
static const float SdfIndirectApproachRetreat = 0.5;
static const uint SdfIndirectApproachSteps = 4u;
// Whether a complete-field sample's clear ball joins the surface sample's, within half the finest spacing: the sample
// is then a certified launch for the receiver whose surface the camera ray reached.
bool sdfIndirectApproachJoined(float3 surfacePoint, float3 sample, float clearance, float surfaceClearance, float spacing) {
    float gap = length(surfacePoint - sample);
    return isfinite(gap) && isfinite(clearance) && isfinite(surfaceClearance) && isfinite(spacing) && spacing > 0.0
        && clearance > 0.0 && surfaceClearance >= 0.0 && gap <= spacing * SdfIndirectApproachRetreat && gap <= clearance + surfaceClearance;
}
#endif
