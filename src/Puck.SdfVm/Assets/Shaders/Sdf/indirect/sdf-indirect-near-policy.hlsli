// The existing temporal phase visits one of the four render-pixel parity classes at High with the cache method.
#ifndef SDF_INDIRECT_NEAR_POLICY_HLSLI
#define SDF_INDIRECT_NEAR_POLICY_HLSLI
#include "../isa/sdf-indirect-layout.hlsli"

bool sdfIndirectNearAdmitted(uint tier, uint method, uint2 pixel, uint historyFrames) {
    return tier == SdfIndirectTierHigh && method == SdfIndirectMethodCache
        && ((pixel.x & 1u) | ((pixel.y & 1u) << 1u)) == historyFrames % SdfIndirectNearPhases;
}
#endif