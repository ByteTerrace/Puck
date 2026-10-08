#define SDF_RECEIVER_PASS
#define SDF_DYNAMIC_TRANSFORMS
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-near-policy.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-near-incoming.hlsli"

[[vk::binding(126, 3)]] StructuredBuffer<float4> nearCases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> nearResults : register(u127, space3);
struct NearProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<NearProbeIndex> nearProbeIndex : register(b0, space4);

// No resolved image, visibility color, specular or atmosphere resource is a test input. A real NearIncoming
// response enters the same resource-free replacement fold called by the production view.
[numthreads(1, 1, 1)]
void CSMain() {
    uint index = nearProbeIndex.index;
    sdfProgramLayout = sdfLoadProgramLayout();
    sdfSecondaryMarchActive = true;
    sdfShadowParticipationActive = true;
    uint before = sdfIndirectEvaluations;
    uint beforeLaunch = sdfIndirectLaunchEvaluations;
    SdfIndirectSources incoming;
    bool hit;
    uint evaluations;
    bool answered = sdfIndirectNearIncoming(nearCases[2u * index].xyz, nearCases[2u * index + 1u].xyz,
        incoming, hit, evaluations);
    nearResults[uint2(index, 0u)] = float4(sdfIndirectSourceTotal(incoming), answered ? 1.0 : 0.0);
    nearResults[uint2(index, 1u)] = float4((float)(sdfIndirectEvaluations - before), (float)evaluations,
        hit ? 1.0 : 0.0, (float)(sdfIndirectLaunchEvaluations - beforeLaunch));
    SdfIndirectSources fallback = (SdfIndirectSources)0;
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        fallback.values[source] = (float)(source + 1u) * float3(2.0, 3.0, 4.0);
    }
    // Poison unanswered output so returning a partial result cannot accidentally pass a zero-only oracle.
    if (!answered) { incoming.values[SdfIndirectSourceEmission] = float3(17.0, 19.0, 23.0); }
    SdfIndirectSources folded = sdfIndirectNearResult(answered, incoming, fallback);
    [unroll] for (uint category = 0u; category < SdfIndirectSourceCount; category++) {
        nearResults[uint2(index, 2u + category)] = float4(folded.values[category], 0.0);
    }
    uint4 masks = 0u;
    [unroll] for (uint phase = 0u; phase < 4u; phase++) {
        [unroll] for (uint parity = 0u; parity < 4u; parity++) {
            uint2 pixel = uint2(parity & 1u, parity >> 1u);
            if (sdfIndirectNearAdmitted(SdfIndirectTierHigh, SdfIndirectMethodCache, pixel, phase)) { masks[phase] |= 1u << parity; }
        }
    }
    nearResults[uint2(index, 7u)] = (float4)masks;
    uint4 exclusions = 0u;
    [unroll] for (uint tick = 0u; tick < 8u; tick++) {
        [unroll] for (uint parity = 0u; parity < 4u; parity++) {
            uint2 pixel = uint2((parity & 1u) + 6u, (parity >> 1u) + 10u);
            exclusions.x += sdfIndirectNearAdmitted(SdfIndirectTierMedium, SdfIndirectMethodCache, pixel, tick) ? 1u : 0u;
            exclusions.y += sdfIndirectNearAdmitted(SdfIndirectTierOff, SdfIndirectMethodCache, pixel, tick) ? 1u : 0u;
            exclusions.z += sdfIndirectNearAdmitted(SdfIndirectTierHigh, SdfIndirectMethodScreen, pixel, tick) ? 1u : 0u;
            exclusions.w += sdfIndirectNearAdmitted(SdfIndirectTierHigh, SdfIndirectMethodCone, pixel, tick) ? 1u : 0u;
        }
    }
    nearResults[uint2(index, 8u)] = (float4)exclusions;

    // These calls observe the real resource helpers independently of an absent continuation directory.
    uint beforeLoads = sdfIndirectLoads;
    uint beforeHashes = sdfIndirectHashes;
    float3 countedDirection = sdfIndirectDirection(int3(2, 3, 5), 0u, 0u);
    nearResults[uint2(index, 9u)] = float4((float)(sdfIndirectLoads - beforeLoads),
        (float)(sdfIndirectHashes - beforeHashes), length(countedDirection), 0.0);
    beforeLoads = sdfIndirectLoads;
    beforeHashes = sdfIndirectHashes;
    int continuedRay = sdfIndirectContinuationRay(0.0, float3(0.0, 0.0, 1.0), int3(2, 3, 5), 0u, 0u, 0.0);
    nearResults[uint2(index, 10u)] = float4((float)(sdfIndirectLoads - beforeLoads),
        (float)(sdfIndirectHashes - beforeHashes), (float)continuedRay, 0.0);
}
