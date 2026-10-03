#ifndef SDF_INDIRECT_MARCH_HLSLI
#define SDF_INDIRECT_MARCH_HLSLI
#include "sdf-indirect-field.hlsli"

struct SdfIndirectRay {
    uint kind;
    float distance;
    float3 normal;
    uint material;
    uint launchHeight;
};

// Acceptance is absolute and certified by a sign bracket, never by the conservative distance alone.
// Normal and bracket queries consume the same whole-ray allowance, including continuation segments.
SdfIndirectRay sdfIndirectMarch(float3 origin, float3 direction, float reach, float farDistance, uint mask, inout uint budget) {
    SdfIndirectRay result = (SdfIndirectRay)0;
    result.kind = SdfIndirectKindUnresolved;
    bool gradientUsed = false;
    float3 normal = 0.0;
    [loop]
    while (budget > 0u) {
        budget--;
        sdfIndirectSteps++;
        float3 position = origin + direction * result.distance;
        uint activeMask = sdfIndirectMasked(result.distance, reach, mask) ? mask : SDF_INSTANCE_MASK_ALL;
        SdfHit sample = sdfIndirectSample(position, activeMask);
        float clearance = sdfMapBallClearance(sample.distance);
        if (!isfinite(sample.distance) || sample.distance < 0.0) { return result; }
        if (sample.distance <= SdfIndirectSurfaceEpsilon) {
            if (activeMask != SDF_INSTANCE_MASK_ALL) {
                if (budget == 0u) { return result; }
                budget--;
                sample = sdfIndirectSample(position, SDF_INSTANCE_MASK_ALL);
                clearance = sdfMapBallClearance(sample.distance);
            }
            if (!gradientUsed && budget > 0u) { budget--; normal = sdfIndirectGradient(position); gradientUsed = true; }
            if (dot(normal, normal) > 0.0 && budget > 0u) {
                budget--;
                SdfHit inside = sdfIndirectSample(position - normal * SdfIndirectSurfaceEpsilon, SDF_INSTANCE_MASK_ALL);
                if (inside.distance <= 0.0) {
                    result.kind = SdfIndirectKindHit;
                    result.normal = normal;
                    result.material = (uint)sample.material;
                    return result;
                }
            }
        }
        if (result.distance >= farDistance && sample.distance > 0.0) { result.kind = SdfIndirectKindExit; return result; }
        float advance = sdfIndirectAdvance(clearance, result.distance, reach, farDistance, mask);
        if (advance <= 0.0) { return result; }
        bool strictlyClearEnd = clearance > farDistance - result.distance;
        result.distance += advance;
        if (result.distance >= farDistance && strictlyClearEnd) { result.kind = SdfIndirectKindExit; return result; }
    }
    return result;
}

// A finite segment is free only when overlapping positive clear balls cover its entire length.
bool sdfIndirectSegment(float3 a, float3 b, inout uint budget, out float3 blockedPoint) {
    blockedPoint = asfloat(0x7fc00000u).xxx;
    float distance = length(b - a);
    if (distance == 0.0) { return true; }
    float3 direction = (b - a) / distance;
    float travel = 0.0;
    [loop]
    while (budget > 0u) {
        budget--;
        sdfIndirectSteps++;
        float3 position = a + direction * travel;
        SdfHit sample = sdfIndirectSample(position, SDF_INSTANCE_MASK_ALL);
        float clearance = sdfMapBallClearance(sample.distance);
        if (sample.distance <= 0.0) { blockedPoint = position; return false; }
        if (!isfinite(clearance) || clearance <= 0.0) { return false; }
        if (clearance > distance - travel) { return true; }
        if (travel >= distance) { return true; }
        if (sample.distance <= SdfIndirectSurfaceEpsilon && budget > 0u) {
            float bracketEnd = min(distance, travel + SdfIndirectSurfaceEpsilon);
            if (bracketEnd > travel) {
                budget--;
                sdfIndirectSteps++;
                SdfHit inside = sdfIndirectSample(a + direction * bracketEnd, SDF_INSTANCE_MASK_ALL);
                if (isfinite(inside.distance) && inside.distance <= 0.0) {
                    float weight = sample.distance / (sample.distance - inside.distance);
                    blockedPoint = a + direction * lerp(travel, bracketEnd, saturate(weight));
                    return false;
                }
            }
        }
        travel = min(distance, travel + clearance);
    }
    return false;
}
#endif
