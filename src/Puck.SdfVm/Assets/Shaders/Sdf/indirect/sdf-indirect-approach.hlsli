// SdfIndirectApproach: a complete-field primary sample supplies the receiver's free-space certificate.
#ifndef SDF_INDIRECT_APPROACH_HLSLI
#define SDF_INDIRECT_APPROACH_HLSLI
float3 sdfIndirectApproachPoint(float3 surfacePoint, float3 direction, uint packed) {
    precise float3 retreat = direction * f16tof32(packed & 65535u);
    precise float3 position = surfacePoint - retreat;
    return position;
}
float sdfIndirectApproachClearance(uint packed) { return f16tof32(packed >> 16u); }
uint sdfIndirectPackApproach(float3 surfacePoint, float3 direction, float3 sample, float clearance,
    float surfaceClearance, float spacing) {
    float distance = dot(surfacePoint - sample, direction);
    if (!isfinite(distance) || !isfinite(clearance) || !isfinite(surfaceClearance) || !isfinite(spacing)
        || spacing <= 0.0 || clearance <= 0.0 || surfaceClearance < 0.0 || distance < 0.0
        || distance > spacing * 0.5 || distance > 65504.0 || length(surfacePoint - sample) > clearance + surfaceClearance) { return 0u; }
    uint offset = f32tof16(distance) & 65535u;
    float3 reconstructed = sdfIndirectApproachPoint(surfacePoint, direction, offset);
    float radius = clearance - length(sample - reconstructed);
    if (!(radius > 0.0) || length(surfacePoint - reconstructed) > spacing * 0.5) { return 0u; }
    uint rounded = f32tof16(min(radius, 65504.0)) & 65535u;
    if (f16tof32(rounded) > radius) { rounded--; }
    float stored = f16tof32(rounded);
    if (!(stored > 0.0) || length(surfacePoint - reconstructed) > stored + surfaceClearance) { return 0u; }
    return offset | (rounded << 16u);
}
#endif
