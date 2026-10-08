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
// The step's sample, its full-field resample, its gradient and its sign witness are the queries of one procedure
// (sdfIndirectMarchStep), which keeps the sample order, the allowance and the counters: a step samples with its active
// mask, resamples the full field when a masked sample lands in the surface band, then takes the gradient once and the
// witness. A failed witness continues the march from the step's last sample.
struct SdfIndirectMarchProc {
    float3 origin;
    float3 direction;
    float reach;
    float farDistance;
    uint mask;
    float sweepRadius;
    uint budget;
    SdfIndirectRay result;
    bool gradientUsed;
    float3 normal;
    uint step;
    uint phase;
    float3 position;
    uint activeMask;
    float witnessOffset;
    SdfHit sample;
    float clearance;
    uint resume;
};
static SdfIndirectMarchProc sdfIndirectMarchProc = (SdfIndirectMarchProc)0;

static const uint SdfIndirectMarchStepPhase = 0u;
static const uint SdfIndirectMarchResamplePhase = 1u;
static const uint SdfIndirectMarchWitnessPhase = 2u;
static const uint SdfIndirectMarchBegun = 0u;
static const uint SdfIndirectMarchSampled = 1u;
static const uint SdfIndirectMarchGradient = 2u;

uint sdfIndirectMarchBegin(float3 origin, float3 direction, float reach, float farDistance, uint mask, float sweepRadius, uint budget) {
    sdfIndirectMarchProc.origin = origin;
    sdfIndirectMarchProc.direction = direction;
    sdfIndirectMarchProc.reach = reach;
    sdfIndirectMarchProc.farDistance = farDistance;
    sdfIndirectMarchProc.mask = mask;
    sdfIndirectMarchProc.sweepRadius = sweepRadius;
    sdfIndirectMarchProc.budget = budget;
    sdfIndirectMarchProc.resume = SdfIndirectMarchBegun;
    return SdfIndirectProcMarch;
}

uint sdfIndirectMarchStep() {
    // The decisions after a sample or a gradient: whether to take the gradient, the witness, or the step's advance.
    bool witness = false;
    bool advance = false;
    if (sdfIndirectMarchProc.resume == SdfIndirectMarchBegun) {
        sdfIndirectMarchProc.result = (SdfIndirectRay)0;
        sdfIndirectMarchProc.result.kind = SdfIndirectKindUnresolved;
        if (!all(isfinite(sdfIndirectMarchProc.origin)) || !all(isfinite(sdfIndirectMarchProc.direction)) || !isfinite(sdfIndirectMarchProc.reach) || !isfinite(sdfIndirectMarchProc.farDistance)
            || !isfinite(sdfIndirectMarchProc.sweepRadius) || sdfIndirectMarchProc.farDistance < 0.0 || sdfIndirectMarchProc.sweepRadius < 0.0) { return SdfIndirectStepReturn; }
        sdfIndirectMarchProc.gradientUsed = false;
        sdfIndirectMarchProc.normal = 0.0;
        sdfIndirectMarchProc.step = 0u;
        sdfIndirectMarchProc.phase = SdfIndirectMarchStepPhase;
        sdfIndirectMarchProc.position = 0.0;
        sdfIndirectMarchProc.activeMask = SDF_INSTANCE_MASK_ALL;
        sdfIndirectMarchProc.witnessOffset = 0.0;
        sdfIndirectMarchProc.sample = (SdfHit)0;
        sdfIndirectMarchProc.clearance = 0.0;
    } else if (sdfIndirectMarchProc.resume == SdfIndirectMarchGradient) {
        sdfIndirectMarchProc.normal = sdfIndirectReplyGradient;
        sdfIndirectMarchProc.gradientUsed = true;
        witness = true;
    } else {
        SdfHit query = sdfIndirectReply;
        if (sdfIndirectMarchProc.phase == SdfIndirectMarchWitnessPhase) {
            if (isfinite(query.distance) && (sdfIndirectMarchProc.sample.distance < 0.0 ? query.distance > 0.0 : query.distance <= 0.0)) {
                sdfIndirectMarchProc.result.kind = SdfIndirectKindHit;
                sdfIndirectMarchProc.result.normal = sdfIndirectMarchProc.normal;
                sdfIndirectMarchProc.result.material = (uint)sdfIndirectMarchProc.sample.material;
                return SdfIndirectStepReturn;
            }
            advance = true;
        } else {
            sdfIndirectMarchProc.sample = query;
            sdfIndirectMarchProc.clearance = sdfMapBallClearance(query.distance) - sdfIndirectMarchProc.sweepRadius;
            if (!isfinite(query.distance) || (query.distance < 0.0 && sdfIndirectMarchProc.result.distance == 0.0)) { return SdfIndirectStepReturn; }
            if (sdfIndirectMarchProc.phase == SdfIndirectMarchResamplePhase || abs(query.distance) <= sdfIndirectMarchProc.sweepRadius + SdfIndirectSurfaceEpsilon) {
                if (sdfIndirectMarchProc.phase == SdfIndirectMarchStepPhase && sdfIndirectMarchProc.activeMask != SDF_INSTANCE_MASK_ALL) {
                    if (sdfIndirectMarchProc.budget == 0u) { return SdfIndirectStepReturn; }
                    sdfIndirectMarchProc.budget--;
                    sdfIndirectMarchProc.phase = SdfIndirectMarchResamplePhase;
                } else if (!sdfIndirectMarchProc.gradientUsed && sdfIndirectMarchProc.budget > 0u) {
                    sdfIndirectMarchProc.budget--;
                    sdfIndirectMarchProc.resume = SdfIndirectMarchGradient;
                    return sdfIndirectAskGradient(sdfIndirectMarchProc.position);
                } else {
                    witness = true;
                }
            } else {
                advance = true;
            }
        }
    }
    if (witness) {
        if (dot(sdfIndirectMarchProc.normal, sdfIndirectMarchProc.normal) > 0.0 && sdfIndirectMarchProc.budget > 0u) {
            sdfIndirectMarchProc.budget--;
            sdfIndirectMarchProc.witnessOffset = (sdfIndirectMarchProc.sample.distance < 0.0 ? 1.0 : -1.0) * (sdfIndirectMarchProc.sweepRadius + SdfIndirectSurfaceEpsilon);
            sdfIndirectMarchProc.phase = SdfIndirectMarchWitnessPhase;
        } else {
            advance = true;
        }
    }
    if (advance) {
        sdfIndirectMarchProc.phase = SdfIndirectMarchStepPhase;
        sdfIndirectMarchProc.step++;
        float distance = sdfIndirectMarchProc.result.distance;
        float farDistance = sdfIndirectMarchProc.farDistance;
        if (distance >= farDistance && sdfIndirectMarchProc.sample.distance > sdfIndirectMarchProc.sweepRadius) { sdfIndirectMarchProc.result.kind = SdfIndirectKindExit; return SdfIndirectStepReturn; }
        float travel = sdfIndirectAdvance(sdfIndirectMarchProc.clearance, distance, sdfIndirectMarchProc.reach, farDistance, sdfIndirectMarchProc.mask);
        if (travel <= 0.0) { return SdfIndirectStepReturn; }
        bool strictlyClearEnd = sdfIndirectMarchProc.clearance > farDistance - distance;
        sdfIndirectMarchProc.result.distance += travel;
        if (sdfIndirectMarchProc.result.distance >= farDistance && strictlyClearEnd) { sdfIndirectMarchProc.result.kind = SdfIndirectKindExit; return SdfIndirectStepReturn; }
    }
    float3 at = sdfIndirectMarchProc.position;
    uint queryMask = SDF_INSTANCE_MASK_ALL;
    if (sdfIndirectMarchProc.phase == SdfIndirectMarchStepPhase) {
        if (!(sdfIndirectMarchProc.step < SdfIndirectLightMarchSteps && sdfIndirectMarchProc.budget > 0u)) { return SdfIndirectStepReturn; }
        sdfIndirectMarchProc.budget--;
        sdfIndirectSteps++;
        sdfIndirectMarchProc.position = sdfIndirectPointAt(sdfIndirectMarchProc.origin, sdfIndirectMarchProc.direction, sdfIndirectMarchProc.result.distance);
        sdfIndirectMarchProc.activeMask = sdfIndirectMasked(sdfIndirectMarchProc.result.distance, sdfIndirectMarchProc.reach, sdfIndirectMarchProc.mask) ? sdfIndirectMarchProc.mask : SDF_INSTANCE_MASK_ALL;
        at = sdfIndirectMarchProc.position;
        queryMask = sdfIndirectMarchProc.activeMask;
    } else if (sdfIndirectMarchProc.phase == SdfIndirectMarchWitnessPhase) {
        at = sdfIndirectPointAt(sdfIndirectMarchProc.position, sdfIndirectMarchProc.normal, sdfIndirectMarchProc.witnessOffset);
    }
    sdfIndirectMarchProc.resume = SdfIndirectMarchSampled;
    return sdfIndirectAsk(at, queryMask);
}

// A finite segment is free only when overlapping positive clear balls cover its entire length. A step's sample and
// its bracket witness are the two queries of one procedure (sdfIndirectSegmentStep); a failed bracket advances from the
// step's sample.
struct SdfIndirectSegmentProc {
    float3 a;
    float3 b;
    uint budget;
    float3 blockedPoint;
    bool clear;
    float distance;
    float3 direction;
    float travel;
    uint step;
    bool bracket;
    float bracketEnd;
    SdfHit sample;
    float clearance;
    bool resuming;
};
static SdfIndirectSegmentProc sdfIndirectSegmentProc = (SdfIndirectSegmentProc)0;

uint sdfIndirectSegmentBegin(float3 a, float3 b, uint budget) {
    sdfIndirectSegmentProc.a = a;
    sdfIndirectSegmentProc.b = b;
    sdfIndirectSegmentProc.budget = budget;
    sdfIndirectSegmentProc.resuming = false;
    return SdfIndirectProcSegment;
}

uint sdfIndirectSegmentStep() {
    if (!sdfIndirectSegmentProc.resuming) {
        sdfIndirectSegmentProc.blockedPoint = asfloat(0x7fc00000u).xxx;
        sdfIndirectSegmentProc.clear = false;
        float3 a = sdfIndirectSegmentProc.a;
        float3 b = sdfIndirectSegmentProc.b;
        sdfIndirectSegmentProc.distance = length(b - a);
        if (!all(isfinite(a)) || !all(isfinite(b)) || !isfinite(sdfIndirectSegmentProc.distance)) { return SdfIndirectStepReturn; }
        if (sdfIndirectSegmentProc.distance == 0.0) { sdfIndirectSegmentProc.clear = true; return SdfIndirectStepReturn; }
        sdfIndirectSegmentProc.direction = (b - a) / sdfIndirectSegmentProc.distance;
        sdfIndirectSegmentProc.travel = 0.0;
        sdfIndirectSegmentProc.step = 0u;
        sdfIndirectSegmentProc.bracket = false;
        sdfIndirectSegmentProc.bracketEnd = 0.0;
        sdfIndirectSegmentProc.sample = (SdfHit)0;
        sdfIndirectSegmentProc.clearance = 0.0;
        sdfIndirectSegmentProc.resuming = true;
    } else {
        SdfHit query = sdfIndirectReply;
        bool advance = true;
        if (sdfIndirectSegmentProc.bracket) {
            sdfIndirectSegmentProc.bracket = false;
            if (isfinite(query.distance) && query.distance <= 0.0) {
                float weight = sdfIndirectSegmentProc.sample.distance / (sdfIndirectSegmentProc.sample.distance - query.distance);
                float travel = sdfIndirectSegmentProc.travel;
                float bracketEnd = sdfIndirectSegmentProc.bracketEnd;
                sdfIndirectSegmentProc.blockedPoint = sdfIndirectPointAt(sdfIndirectSegmentProc.a, sdfIndirectSegmentProc.direction, lerp(travel, bracketEnd, saturate(weight)));
                return SdfIndirectStepReturn;
            }
        } else {
            sdfIndirectSegmentProc.sample = query;
            sdfIndirectSegmentProc.clearance = sdfMapBallClearance(query.distance);
            float distance = sdfIndirectSegmentProc.distance;
            float travel = sdfIndirectSegmentProc.travel;
            if (query.distance <= 0.0) { sdfIndirectSegmentProc.blockedPoint = sdfIndirectPointAt(sdfIndirectSegmentProc.a, sdfIndirectSegmentProc.direction, travel); return SdfIndirectStepReturn; }
            if (!isfinite(sdfIndirectSegmentProc.clearance) || sdfIndirectSegmentProc.clearance <= 0.0) { return SdfIndirectStepReturn; }
            if (sdfIndirectSegmentProc.clearance > distance - travel) { sdfIndirectSegmentProc.clear = true; return SdfIndirectStepReturn; }
            if (travel >= distance) { sdfIndirectSegmentProc.clear = true; return SdfIndirectStepReturn; }
            if (query.distance <= SdfIndirectSurfaceEpsilon && sdfIndirectSegmentProc.budget > 0u) {
                sdfIndirectSegmentProc.bracketEnd = min(distance, travel + SdfIndirectSurfaceEpsilon);
                if (sdfIndirectSegmentProc.bracketEnd > travel) {
                    sdfIndirectSegmentProc.budget--;
                    sdfIndirectSteps++;
                    sdfIndirectSegmentProc.bracket = true;
                    advance = false;
                }
            }
        }
        if (advance) {
            sdfIndirectSegmentProc.travel = min(sdfIndirectSegmentProc.distance, sdfIndirectSegmentProc.travel + sdfIndirectSegmentProc.clearance);
            sdfIndirectSegmentProc.step++;
        }
    }
    float3 position;
    if (!sdfIndirectSegmentProc.bracket) {
        if (!(sdfIndirectSegmentProc.step < SdfIndirectSegmentSteps && sdfIndirectSegmentProc.budget > 0u)) { return SdfIndirectStepReturn; }
        sdfIndirectSegmentProc.budget--;
        sdfIndirectSteps++;
        position = sdfIndirectPointAt(sdfIndirectSegmentProc.a, sdfIndirectSegmentProc.direction, sdfIndirectSegmentProc.travel);
    } else {
        position = sdfIndirectPointAt(sdfIndirectSegmentProc.a, sdfIndirectSegmentProc.direction, sdfIndirectSegmentProc.bracketEnd);
    }
    return sdfIndirectAsk(position, SDF_INSTANCE_MASK_ALL);
}

#ifdef SDF_INDIRECT_PROC_MARCH
SdfIndirectRay sdfIndirectMarch(float3 origin, float3 direction, float reach, float farDistance, uint mask, float sweepRadius, inout uint budget) {
    sdfIndirectRun(sdfIndirectMarchBegin(origin, direction, reach, farDistance, mask, sweepRadius, budget));
    budget = sdfIndirectMarchProc.budget;
    return sdfIndirectMarchProc.result;
}
#endif
#ifdef SDF_INDIRECT_PROC_SEGMENT
bool sdfIndirectSegment(float3 a, float3 b, inout uint budget, out float3 blockedPoint) {
    sdfIndirectRun(sdfIndirectSegmentBegin(a, b, budget));
    budget = sdfIndirectSegmentProc.budget;
    blockedPoint = sdfIndirectSegmentProc.blockedPoint;
    return sdfIndirectSegmentProc.clear;
}
#endif
#endif
