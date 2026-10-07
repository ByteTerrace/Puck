#ifndef FIELD_SDF_TAPE_HLSLI
#define FIELD_SDF_TAPE_HLSLI

static uint sdfTapeBase = 0u;
static uint sdfTapeInstanceMask = SDF_INSTANCE_MASK_ALL;
static bool sdfTapeActive = false;
static bool sdfTapeSampleActive = false;
static bool sdfTapeTrackMaterial = false;
static uint sdfTapeSlab = 0u;

uint sdfTapeMaskWords() { return (sdfProgramLayout.segmentCount + 31u) >> 5u; }
uint sdfTapeSummaryWords() { return (sdfTapeMaskWords() + 31u) >> 5u; }
uint sdfTapeInstructionWords() { return (SDF_INSTANCE_TAPE_TOKENS(sdfProgramWord(sdfProgramLayout.instanceOffset)) + 31u) >> 5u; }
uint sdfTapeToken(uint instruction) {
    uint certificates = SDF_SEGMENT_TAPE_OFFSET(sdfProgramWord(sdfProgramLayout.segmentOffset));
    return sdfProgramWord(certificates + instruction).w >> SDF_TAPE_TOKEN_SHIFT;
}
uint sdfTapeSlabWords() { return sdfTapeMaskWords() + sdfTapeSummaryWords() + 2u * sdfTapeInstructionWords(); }
uint sdfTapeStride() { return SDF_TAPE_HEADER_WORDS + SDF_TAPE_SLAB_COUNT * sdfTapeSlabWords(); }
uint sdfTapeMaskBase() { return sdfTapeBase + SDF_TAPE_HEADER_WORDS + sdfTapeSlab * sdfTapeSlabWords(); }
uint sdfTapeInstructionBase() { return sdfTapeMaskBase() + sdfTapeMaskWords() + sdfTapeSummaryWords(); }
uint sdfTapeDecisionBase() { return sdfTapeInstructionBase() + sdfTapeInstructionWords(); }

#if defined(SDF_SEGMENT_TAPES) && !defined(SDF_TAPE_REFERENCE)
uint sdfTapeRead(uint offset) {
#ifdef SDF_TAPE_BUILD
    // A device law builds and consumes one tape in a dispatch. Reading the UAV
    // keeps that buffer in one resource state after its device-memory barrier.
    return sdfSegmentTapesRW[offset];
#else
    return sdfSegmentTapes[offset];
#endif
}
#endif

void sdfTapeSample(float3 position, uint instanceMask) {
    sdfTapeSampleActive = false;
#if defined(SDF_SEGMENT_TAPES) && !defined(SDF_TAPE_REFERENCE)
    if (!sdfTapeActive || instanceMask != sdfTapeInstanceMask || sdfSecondaryMarchActive ||
        (sdfDetailShadingActive && !sdfProgramLayout.noDetailShapes)) { return; }
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (sdfShadowParticipationActive || sdfIndirectParticipationActive) { return; }
#endif
#ifdef SDF_SCREEN_SOURCES
    if (sdfShadowMaskActive || sdfAmbientMaskActive) { return; }
#endif
    // A surface probe or secondary ray may leave the camera's covered balls.
    [loop]
    for (uint slab = 0u; slab < SDF_TAPE_SLAB_COUNT; slab++) {
        uint offset = sdfTapeBase + 1u + 4u * slab;
        float3 delta = position - asfloat(uint3(sdfTapeRead(offset), sdfTapeRead(offset + 1u), sdfTapeRead(offset + 2u)));
        float radius = asfloat(sdfTapeRead(offset + 3u));
        // The inward guard covers the subtract, products and reduction, including subnormal flush.
        float magnitude = max(max(abs(position.x), abs(position.y)), abs(position.z));
        float guard = (magnitude + radius + 1.0) * SDF_TAPE_BLEND_ROUNDOFF_MARGIN;
        if (length(delta) + guard < radius) {
            sdfTapeSampleActive = true;
            sdfTapeSlab = slab;
            return;
        }
    }
#endif
}

bool sdfTapeShapeLive(uint instruction) {
#if defined(SDF_SEGMENT_TAPES) && !defined(SDF_TAPE_REFERENCE)
    if (!sdfTapeSampleActive) { return true; }
    uint token = sdfTapeToken(instruction);
    return (sdfTapeRead(sdfTapeInstructionBase() + (token >> 5u)) & (1u << (token & 31u))) != 0u;
#else
    return true;
#endif
}
// On a shape this bit means its candidate strictly decides the field. On PopField it means the parent decides.
bool sdfTapeDecided(uint instruction) {
#if defined(SDF_SEGMENT_TAPES) && !defined(SDF_TAPE_REFERENCE)
    if (!sdfTapeSampleActive) { return false; }
    uint token = sdfTapeToken(instruction);
    return (sdfTapeRead(sdfTapeDecisionBase() + (token >> 5u)) & (1u << (token & 31u))) != 0u;
#else
    return false;
#endif
}
float sdfTapeWinnerSeed(uint blend) {
    return (blend == SDF_BLEND_UNION || blend == SDF_BLEND_SMOOTH_UNION) ? SDF_STEP_BOUND_NONE : -SDF_STEP_BOUND_NONE;
}
void sdfTapeSkippedShape(uint4 header) {
    uint blend = SDF_INSTRUCTION_BLEND(header);
    if (sdfTapeTrackMaterial && (blend == SDF_BLEND_SMOOTH_UNION || blend == SDF_BLEND_SMOOTH_INTERSECTION || blend == SDF_BLEND_SMOOTH_SUBTRACTION)) {
        sdfMaterialBlendWeight = 0.0;
        sdfMaterialBlendOther = (int)SDF_INSTRUCTION_MATERIAL(header);
    }
}

uint sdfTapeNextSegment(uint segment, uint end) {
#if defined(SDF_SEGMENT_TAPES) && !defined(SDF_TAPE_REFERENCE)
    // Material queries retain the ordinary bound tests: a bound-skipped smooth loser leaves the prior seam intact,
    // while an evaluated smooth loser clears it. Instruction masks still remove its primitive evaluation.
    if (sdfTapeSampleActive && !sdfTapeTrackMaterial && segment < end) {
        uint word = segment >> 5u;
        uint words = sdfTapeMaskWords();
        uint maskBase = sdfTapeMaskBase();
        uint bits = sdfTapeRead(maskBase + word) & (0xFFFFFFFFu << (segment & 31u));
        [loop]
        while (bits == 0u && ++word < words) {
            uint summary = word >> 5u;
            uint nonempty = sdfTapeRead(maskBase + words + summary) & (0xFFFFFFFFu << (word & 31u));
            [loop]
            while (nonempty == 0u && ++summary < sdfTapeSummaryWords()) {
                nonempty = sdfTapeRead(maskBase + words + summary);
            }
            if (nonempty == 0u) { return end; }
            word = (summary << 5u) + firstbitlow(nonempty);
            bits = sdfTapeRead(maskBase + word);
        }
        uint next = bits == 0u ? end : min(end, (word << 5u) + firstbitlow(bits));
        return next;
    }
#endif
    return segment;
}

#ifdef SDF_TAPE_BUILD
static bool sdfTapeBuilding = false;
static float sdfTapeRadius = 0.0;
static float sdfTapeMagnitude = 0.0;
static float sdfTapeCentreMagnitude = 0.0;
static float2 sdfTapeInterval;
static float2 sdfTapeSavedIntervals[SDF_MAX_FIELD_SCOPE_DEPTH];
static uint sdfTapeScopeStarts[SDF_MAX_FIELD_SCOPE_DEPTH];
static uint sdfTapeScopeDepth = 0u;
static bool sdfTapeFieldKnown = true;
static bool sdfTapeSavedKnown[SDF_MAX_FIELD_SCOPE_DEPTH];
static uint sdfTapeUnionStart = 0u;
static bool sdfTapeSegmentNeeded = false;
static bool sdfTapeSegmentOmissible = false;
static bool sdfTapeSegmentUnion = false;
static uint sdfTapeShapeCount = 0u;

bool sdfTapeFinite(float value) { return (asuint(value) & 0x7F800000u) != 0x7F800000u; }
bool sdfTapeEndpoint(float value) { return abs(value) < SDF_STEP_BOUND_NONE; }
float sdfTapeUp(float value) {
    if (abs(value) >= SDF_STEP_BOUND_NONE) { return clamp(value, -SDF_STEP_BOUND_NONE, SDF_STEP_BOUND_NONE); }
    return value == 0.0 ? asfloat(0x00800000u) : asfloat(asuint(value) + (value > 0.0 ? 1u : 0xFFFFFFFFu));
}
float sdfTapeDown(float value) { return -sdfTapeUp(-value); }
float sdfTapeAddUp(float a, float b) {
    if (!sdfTapeEndpoint(a)) { return a; }
    if (!sdfTapeEndpoint(b)) { return b; }
    return sdfTapeUp(a + b);
}
float sdfTapeMulUp(float a, float b) { return sdfTapeUp(a * b); }
// DXC's finite-math mode must never see IEEE infinities. These sentinels are ordered beyond every admitted
// certificate (whose intermediate bound is 1e12); endpoint arithmetic preserves them as unbounded markers.
float2 sdfTapeUnknown() { return float2(-SDF_STEP_BOUND_NONE, SDF_STEP_BOUND_NONE); }
uint sdfTapeWorkBase() { return sdfTapeInstructionBase(); }

void sdfTapeBeginSegment() {
    if (!sdfTapeBuilding) { return; }
    sdfTapeSegmentNeeded = false;
    sdfTapeSegmentOmissible = true;
    sdfTapeSegmentUnion = true;
    sdfTapeShapeCount = 0u;
}
void sdfTapeEndSegment(uint segment) {
    if (!sdfTapeBuilding) { return; }
    if (sdfTapeSegmentNeeded || !sdfTapeSegmentOmissible || sdfTapeShapeCount == 0u) {
        uint word = sdfTapeMaskBase() + (segment >> 5u);
        sdfSegmentTapesRW[word] |= 1u << (segment & 31u);
    }
}
void sdfTapeForgetField(uint instruction, bool seed) {
    if (!sdfTapeBuilding) { return; }
    if (seed) {
        sdfTapeSavedIntervals[sdfTapeScopeDepth] = sdfTapeInterval;
        sdfTapeSavedKnown[sdfTapeScopeDepth] = sdfTapeFieldKnown;
        sdfTapeScopeStarts[sdfTapeScopeDepth++] = instruction + 1u;
    }
    sdfTapeInterval = seed ? float2(SDF_FAR_DISTANCE, SDF_FAR_DISTANCE) : sdfTapeUnknown();
    sdfTapeFieldKnown = seed;
    sdfTapeUnionStart = instruction + 1u;
    sdfTapeSegmentOmissible = false;
}
void sdfTapeClearUnionPrefix(uint instruction) {
    uint endToken = sdfTapeToken(instruction);
    [loop]
    for (uint first = sdfTapeToken(sdfTapeUnionStart); first < endToken;) {
        uint word = first >> 5u;
        uint end = min(endToken, (word + 1u) << 5u);
        uint mask = (0xFFFFFFFFu << (first & 31u));
        if ((end & 31u) != 0u) { mask &= (1u << (end & 31u)) - 1u; }
        sdfSegmentTapesRW[sdfTapeWorkBase() + word] &= ~mask;
        first = end;
    }
}
struct SdfTapeBlend {
    float2 interval;
    bool loses;
    bool wins;
    bool modeled;
};
SdfTapeBlend sdfTapeBlend(float2 current, float2 candidate, uint blend, float smooth) {
    SdfTapeBlend result;
    result.interval = sdfTapeUnknown();
    result.loses = false;
    result.wins = false;
    bool minimum = blend == SDF_BLEND_UNION || blend == SDF_BLEND_SMOOTH_UNION;
    result.modeled = minimum || blend == SDF_BLEND_INTERSECTION || blend == SDF_BLEND_SMOOTH_INTERSECTION ||
        blend == SDF_BLEND_SUBTRACTION || blend == SDF_BLEND_SMOOTH_SUBTRACTION;
    if (!result.modeled) { return result; }
    if (blend == SDF_BLEND_SUBTRACTION || blend == SDF_BLEND_SMOOTH_SUBTRACTION) { candidate = -candidate.yx; }
    bool softened = blend == SDF_BLEND_SMOOTH_UNION || blend == SDF_BLEND_SMOOTH_INTERSECTION || blend == SDF_BLEND_SMOOTH_SUBTRACTION;
    float band = softened ? sdfTapeUp(max(smooth, SDF_SMOOTH_RADIUS_MIN)) : 0.0;
    float magnitude = max(max(sdfTapeEndpoint(candidate.x) ? abs(candidate.x) : 0.0, sdfTapeEndpoint(candidate.y) ? abs(candidate.y) : 0.0),
        max(sdfTapeEndpoint(current.x) ? abs(current.x) : 0.0, sdfTapeEndpoint(current.y) ? abs(current.y) : 0.0));
    float gap = sdfTapeMulUp(sdfTapeAddUp(magnitude, band), SDF_TAPE_BLEND_ROUNDOFF_MARGIN);
    float separation = sdfTapeAddUp(band, gap);
    result.loses = minimum ? candidate.x > sdfTapeAddUp(current.y, separation)
                           : candidate.y < -sdfTapeAddUp(-current.x, separation);
    result.wins = minimum ? candidate.y < -sdfTapeAddUp(-current.x, separation)
                          : candidate.x > sdfTapeAddUp(current.y, separation);
    if (result.loses) { result.interval = current; }
    else if (result.wins) { result.interval = candidate; }
    else {
        float2 combined = minimum ? min(current, candidate) : max(current, candidate);
        float allowance = softened ? sdfTapeAddUp(sdfTapeMulUp(band, 0.25), gap) : 0.0;
        result.interval = float2(-sdfTapeAddUp(-combined.x, allowance), sdfTapeAddUp(combined.y, allowance));
    }
    return result;
}
void sdfTapePop(uint instruction, uint blend, float4 data) {
    if (!sdfTapeBuilding) { return; }
    uint scope = --sdfTapeScopeDepth;
    float scale = data.y > 0.0 ? data.y : 1.0;
    float2 child = float2(sdfTapeEndpoint(sdfTapeInterval.x) ? -sdfTapeMulUp(-sdfTapeInterval.x, scale) : sdfTapeInterval.x,
        sdfTapeEndpoint(sdfTapeInterval.y) ? sdfTapeMulUp(sdfTapeInterval.y, scale) : sdfTapeInterval.y);
    SdfTapeBlend composed = sdfTapeBlend(sdfTapeSavedIntervals[scope], child, blend, data.x);
    // Independent part marches query each child's full field. Keep those children even when their root union loses.
    if (composed.loses && sdfTapeFieldKnown && sdfProgramLayout.partProgramOffset == 0u) {
        sdfTapeUnionStart = sdfTapeScopeStarts[scope];
        sdfTapeClearUnionPrefix(instruction);
        uint token = sdfTapeToken(instruction);
        sdfSegmentTapesRW[sdfTapeDecisionBase() + (token >> 5u)] |= 1u << (token & 31u);
    }
    sdfTapeInterval = composed.interval;
    sdfTapeFieldKnown = sdfTapeFieldKnown && sdfTapeSavedKnown[scope] && composed.modeled;
    sdfTapeUnionStart = instruction + 1u;
    sdfTapeSegmentOmissible = false;
}
void sdfTapeUnary(uint instruction, uint op, float amount) {
    if (!sdfTapeBuilding) { return; }
    // A finite authored offset can exceed the certificate domain. Treat it as unknown before sentinel arithmetic;
    // huge opposite offsets can cancel back to a small field, so a sticky infinity would discard real winners.
    if (!sdfTapeFinite(amount) || abs(amount) > SDF_TAPE_INTERMEDIATE_LIMIT ||
        any(abs(sdfTapeInterval) > SDF_TAPE_INTERMEDIATE_LIMIT)) {
        sdfTapeForgetField(instruction, false);
        return;
    }
    if (op == SDF_OP_ONION) {
        bool crosses = sdfTapeInterval.x <= 0.0 && sdfTapeInterval.y >= 0.0;
        sdfTapeInterval = float2(crosses ? 0.0 : min(abs(sdfTapeInterval.x), abs(sdfTapeInterval.y)),
            max(abs(sdfTapeInterval.x), abs(sdfTapeInterval.y)));
    }
    sdfTapeInterval = float2(-sdfTapeAddUp(-sdfTapeInterval.x, amount), sdfTapeAddUp(sdfTapeInterval.y, -amount));
    if (any(abs(sdfTapeInterval) > SDF_TAPE_INTERMEDIATE_LIMIT)) {
        sdfTapeForgetField(instruction, false);
        return;
    }
    sdfTapeUnionStart = instruction + 1u;
    sdfTapeSegmentOmissible = false;
}
// The dynamic-frame counterpart of SdfTapeCertificate.TransformBall. Every positive operation rounds outward;
// the pose polynomial's operation budget is the host TapeMetric's translate/rotate model in float units.
bool sdfTapeDynamicBall(int slot, inout float radius, inout float magnitude) {
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (slot <= SDF_TRANSFORM_SLOT_NONE) { return false; }
    float3 position = sdfDynamicTransformRow(3u * (uint)slot).xyz;
    float4 q = sdfDynamicTransformRow(3u * (uint)slot + 1u);
    float qxy = sdfTapeAddUp(sdfTapeMulUp(q.x, q.x), sdfTapeMulUp(q.y, q.y));
    float s = sdfTapeAddUp(qxy, sdfTapeMulUp(q.z, q.z));
    float squared = sdfTapeAddUp(s, sdfTapeMulUp(q.w, q.w));
    if (!sdfTapeFinite(squared) || squared > 16.0 || any(abs(position) > SDF_TAPE_COORDINATE_LIMIT)) { return false; }
    // For r=|q|^2, the polynomial's squared norm is max(1,1+4*s*(r-1)) with s<=r.
    // Therefore max(1,2*r-1) bounds its norm without a hardware sqrt accuracy assumption.
    float norm = max(1.0, sdfTapeUp(sdfTapeMulUp(2.0, squared) - 1.0));
    float translation = sdfTapeAddUp(sdfTapeAddUp(abs(position.x), abs(position.y)), abs(position.z));
    float rootThree = 1.75;
    float translated = sdfTapeAddUp(sdfTapeMulUp(rootThree, magnitude), translation);
    float initialError = sdfTapeAddUp(translated, 1.0);
    float cost = sdfTapeMulUp(32.0, sdfTapeAddUp(1.0, sdfTapeMulUp(8.0, squared)));
    float units = sdfTapeMulUp(norm, sdfTapeAddUp(initialError, sdfTapeMulUp(cost,
        sdfTapeAddUp(sdfTapeAddUp(translated, sdfTapeMulUp(1.0 / 65536.0, initialError)), 1.0))));
    float poseError = sdfTapeMulUp(units, 1.0 / 16777216.0);
    radius = sdfTapeAddUp(sdfTapeMulUp(norm, radius), sdfTapeMulUp(2.0, poseError));
    magnitude = sdfTapeAddUp(sdfTapeAddUp(sdfTapeMulUp(norm,
        sdfTapeAddUp(sdfTapeMulUp(rootThree, sdfTapeCentreMagnitude), translation)), poseError), radius);
    return sdfTapeFinite(radius) && sdfTapeFinite(magnitude) && magnitude <= SDF_TAPE_COORDINATE_LIMIT;
#else
    return false;
#endif
}
void sdfTapeCandidate(uint segment, uint instruction, float candidate, uint blend, float smooth, int slot, float powerExponent) {
    if (!sdfTapeBuilding) { return; }
    uint certificateOffset = SDF_SEGMENT_TAPE_OFFSET(sdfProgramWord(sdfProgramLayout.segmentOffset));
    uint4 certificate = sdfProgramWord(certificateOffset + instruction);
    bool known = (certificate.w & SDF_TAPE_CERTIFIED) != 0u;
    bool omissible = (certificate.w & SDF_TAPE_OMISSIBLE) != 0u;
    sdfTapeSegmentOmissible = sdfTapeSegmentOmissible && omissible;
    sdfTapeShapeCount++;
    float2 interval = sdfTapeUnknown();
    float radius = sdfTapeRadius;
    float magnitudeAtShape = sdfTapeMagnitude;
    if ((certificate.w & SDF_TAPE_DYNAMIC_FRAME) != 0u) { known = known && sdfTapeDynamicBall(slot, radius, magnitudeAtShape); }
    known = known && sdfTapeFinite(candidate) && magnitudeAtShape <= SDF_TAPE_COORDINATE_LIMIT;
    float normGain = 0.0;
    if ((certificate.w & SDF_TAPE_CENTERED_NORM_ENVELOPE) != 0u) {
        known = known && sdfTapeFinite(powerExponent) && powerExponent > 2.0 && powerExponent <= 3.0;
        if (known) {
            float gap = sdfTapeMulUp(sdfTapeUp(7.0 / 25.0), sdfTapeUp(powerExponent - 2.0));
            normGain = sdfTapeUp(0.5 * sdfTapeUp(gap / sdfTapeDown(1.0 - gap)));
        }
    }
    if (known) {
        float3 bounds = asfloat(certificate.xyz);
        float error = sdfTapeAddUp(sdfTapeMulUp(bounds.y, magnitudeAtShape), bounds.z);
        // The packed coefficients include arithmetic/alpha and gap*scaledMinimumRadius/(2*alpha).
        // The computed centre value supplies the remaining norm-envelope term without a global point-magnitude guess.
        error = sdfTapeAddUp(error, sdfTapeMulUp(normGain, abs(candidate)));
        float reach = sdfTapeAddUp(sdfTapeMulUp(bounds.x, radius), sdfTapeMulUp(2.0, error));
        interval = float2(sdfTapeDown(candidate - reach), sdfTapeUp(candidate + reach));
    }
    bool unionBlend = blend == SDF_BLEND_UNION || blend == SDF_BLEND_SMOOTH_UNION;
    sdfTapeSegmentUnion = sdfTapeSegmentUnion && unionBlend;
    SdfTapeBlend decision = sdfTapeBlend(sdfTapeInterval, interval, blend, smooth);
    sdfTapeFieldKnown = sdfTapeFieldKnown && known && decision.modeled;
    if (decision.loses && known) { return; }
    sdfTapeSegmentNeeded = true;
    uint token = certificate.w >> SDF_TAPE_TOKEN_SHIFT;
    sdfSegmentTapesRW[sdfTapeWorkBase() + (token >> 5u)] |= 1u << (token & 31u);
    if (decision.wins && known) {
        sdfTapeClearUnionPrefix(instruction);
        sdfSegmentTapesRW[sdfTapeDecisionBase() + (token >> 5u)] |= 1u << (token & 31u);
    }
    sdfTapeInterval = decision.interval;
    // A later strict union winner may discard only an uninterrupted union dependency. Field operations and
    // scope boundaries reset it separately; an unsupported candidate always remains in the tape.
    if (!unionBlend || !known) { sdfTapeUnionStart = instruction + 1u; }
}
#endif
#endif
