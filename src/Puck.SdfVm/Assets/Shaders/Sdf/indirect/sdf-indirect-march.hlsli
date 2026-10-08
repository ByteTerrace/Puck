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

// A contracted multiply-add can move a sample across the field's zero boundary. Match primary's two rounded
// operations at every indirect sample and bracket witness, so backend contraction cannot change its certificate.
float3 sdfIndirectPointAt(float3 origin, float3 direction, float distance) {
    precise float3 advance = direction * distance;
    precise float3 position = origin + advance;
    return position;
}

// Acceptance is absolute and certified by a sign bracket, never by the conservative distance alone.
// A rounded advance may land just inside a surface; its witness points outward. An inside origin stays unresolved.
// Normal and bracket queries consume the same whole-ray allowance, including continuation segments. Radius zero
// is the cache's point ray; a light-camera texel subtracts its conservative sphere radius from every clear advance
// and extends the sign witness by that radius, without replacing the certificate by a small distance alone.
// The step's sample, its full-field resample and its sign witness are phases of one loop around one field call site,
// since each sdfIndirectSample call inlines the complete interpreter in DXIL. The phases keep the sample order, the
// allowance and the counters: a step samples with its active mask, resamples the full field when a masked sample
// lands in the surface band, then takes the gradient once and the witness. A failed witness continues the march
// from the step's last sample.
SdfIndirectRay sdfIndirectMarch(float3 origin, float3 direction, float reach, float farDistance, uint mask, float sweepRadius, inout uint budget) {
    static const uint StepPhase = 0u;
    static const uint ResamplePhase = 1u;
    static const uint WitnessPhase = 2u;
    SdfIndirectRay result = (SdfIndirectRay)0;
    result.kind = SdfIndirectKindUnresolved;
    if (!all(isfinite(origin)) || !all(isfinite(direction)) || !isfinite(reach) || !isfinite(farDistance)
        || !isfinite(sweepRadius) || farDistance < 0.0 || sweepRadius < 0.0) { return result; }
    bool gradientUsed = false;
    float3 normal = 0.0;
    uint step = 0u;
    uint phase = StepPhase;
    float3 position = 0.0;
    uint activeMask = SDF_INSTANCE_MASK_ALL;
    float witnessOffset = 0.0;
    SdfHit sample = (SdfHit)0;
    float clearance = 0.0;
    [loop]
    for (;;) {
        float3 at = position;
        uint queryMask = SDF_INSTANCE_MASK_ALL;
        if (phase == StepPhase) {
            if (!(step < SdfIndirectLightMarchSteps && budget > 0u)) { break; }
            budget--;
            sdfIndirectSteps++;
            position = sdfIndirectPointAt(origin, direction, result.distance);
            activeMask = sdfIndirectMasked(result.distance, reach, mask) ? mask : SDF_INSTANCE_MASK_ALL;
            at = position;
            queryMask = activeMask;
        } else if (phase == WitnessPhase) {
            at = sdfIndirectPointAt(position, normal, witnessOffset);
        }
        SdfHit query = sdfIndirectSample(at, queryMask);
        if (phase == WitnessPhase) {
            if (isfinite(query.distance) && (sample.distance < 0.0 ? query.distance > 0.0 : query.distance <= 0.0)) {
                result.kind = SdfIndirectKindHit;
                result.normal = normal;
                result.material = (uint)sample.material;
                return result;
            }
        } else {
            sample = query;
            clearance = sdfMapBallClearance(sample.distance) - sweepRadius;
            if (!isfinite(sample.distance) || (sample.distance < 0.0 && result.distance == 0.0)) { return result; }
            if (phase == ResamplePhase || abs(sample.distance) <= sweepRadius + SdfIndirectSurfaceEpsilon) {
                if (phase == StepPhase && activeMask != SDF_INSTANCE_MASK_ALL) {
                    if (budget == 0u) { return result; }
                    budget--;
                    phase = ResamplePhase;
                    continue;
                }
                if (!gradientUsed && budget > 0u) { budget--; normal = sdfIndirectGradient(position); gradientUsed = true; }
                if (dot(normal, normal) > 0.0 && budget > 0u) {
                    budget--;
                    witnessOffset = (sample.distance < 0.0 ? 1.0 : -1.0) * (sweepRadius + SdfIndirectSurfaceEpsilon);
                    phase = WitnessPhase;
                    continue;
                }
            }
        }
        phase = StepPhase;
        step++;
        if (result.distance >= farDistance && sample.distance > sweepRadius) { result.kind = SdfIndirectKindExit; return result; }
        float advance = sdfIndirectAdvance(clearance, result.distance, reach, farDistance, mask);
        if (advance <= 0.0) { return result; }
        bool strictlyClearEnd = clearance > farDistance - result.distance;
        result.distance += advance;
        if (result.distance >= farDistance && strictlyClearEnd) { result.kind = SdfIndirectKindExit; return result; }
    }
    return result;
}

// A finite segment is free only when overlapping positive clear balls cover its entire length. A step's sample and
// its bracket witness are two phases of one field call site; a failed bracket advances from the step's sample.
bool sdfIndirectSegment(float3 a, float3 b, inout uint budget, out float3 blockedPoint) {
    blockedPoint = asfloat(0x7fc00000u).xxx;
    float distance = length(b - a);
    if (!all(isfinite(a)) || !all(isfinite(b)) || !isfinite(distance)) { return false; }
    if (distance == 0.0) { return true; }
    float3 direction = (b - a) / distance;
    float travel = 0.0;
    uint step = 0u;
    bool bracket = false;
    float bracketEnd = 0.0;
    SdfHit sample = (SdfHit)0;
    float clearance = 0.0;
    [loop]
    for (;;) {
        float3 position;
        if (!bracket) {
            if (!(step < SdfIndirectSegmentSteps && budget > 0u)) { break; }
            budget--;
            sdfIndirectSteps++;
            position = sdfIndirectPointAt(a, direction, travel);
        } else {
            position = sdfIndirectPointAt(a, direction, bracketEnd);
        }
        SdfHit query = sdfIndirectSample(position, SDF_INSTANCE_MASK_ALL);
        if (bracket) {
            bracket = false;
            if (isfinite(query.distance) && query.distance <= 0.0) {
                float weight = sample.distance / (sample.distance - query.distance);
                blockedPoint = sdfIndirectPointAt(a, direction, lerp(travel, bracketEnd, saturate(weight)));
                return false;
            }
        } else {
            sample = query;
            clearance = sdfMapBallClearance(sample.distance);
            if (sample.distance <= 0.0) { blockedPoint = position; return false; }
            if (!isfinite(clearance) || clearance <= 0.0) { return false; }
            if (clearance > distance - travel) { return true; }
            if (travel >= distance) { return true; }
            if (sample.distance <= SdfIndirectSurfaceEpsilon && budget > 0u) {
                bracketEnd = min(distance, travel + SdfIndirectSurfaceEpsilon);
                if (bracketEnd > travel) {
                    budget--;
                    sdfIndirectSteps++;
                    bracket = true;
                    continue;
                }
            }
        }
        travel = min(distance, travel + clearance);
        step++;
    }
    return false;
}
#endif
