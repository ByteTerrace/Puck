// The receiver's approach: a sphere-traced chain of complete-field samples along its camera ray, from half the finest
// spacing short of the surface, each sample's clear ball reaching the next. The chain joins the receiver when a
// sample's ball reaches the surface point within its slack: the larger of the surface sample's own clearance and the
// primary acceptance threshold the surface point was accepted within. Its first sample, the farthest and widest ball,
// is then the receiver's certified launch. The receiver pass marches it (sdf-indirect-receiver.hlsli) before a shared
// proof can read a canonical record; a receiver whose chain never joins launches along its normal under the shared
// admission instead.
#ifndef SDF_INDIRECT_APPROACH_HLSLI
#define SDF_INDIRECT_APPROACH_HLSLI
// The chain starts this fraction of the finest spacing back along the camera ray and takes at most this many samples,
// after the surface sample. A chain reaches the surface geometrically, at a rate set by the ray's incidence.
static const float SdfIndirectApproachRetreat = 0.5;
static const uint SdfIndirectApproachSteps = 8u;
// The surface point's slack, or a negative value when the complete field puts it deeper inside than its acceptance
// threshold allows, which no chain can join.
float sdfIndirectApproachSlack(float surfaceClearance, float threshold) {
    if (!isfinite(surfaceClearance) || !isfinite(threshold) || threshold < 0.0 || surfaceClearance < -threshold) { return -1.0; }
    return max(max(surfaceClearance, 0.0), threshold);
}
// Whether a chain sample's clear ball reaches the surface point within its slack.
bool sdfIndirectApproachJoined(float3 surfacePoint, float3 sample, float clearance, float slack) {
    float gap = length(surfacePoint - sample);
    return isfinite(gap) && isfinite(clearance) && clearance > 0.0 && slack >= 0.0 && gap <= clearance + slack;
}
#endif
