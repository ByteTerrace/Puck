// Views applies the receiver pass's answer (sdf-indirect-receiver.hlsli): at a certified receiver it reads the complete
// lighting bank at the certificate's launch with its shading normal, or takes the near-field or comparison answer that
// replaces it. It performs no field query; the receiver pass owns every proof and replacement.
#ifndef SDF_INDIRECT_APPLY_HLSLI
#define SDF_INDIRECT_APPLY_HLSLI
#include "sdf-indirect-irradiance.hlsli"
#include "../isa/sdf-sky-kinds.hlsli"
#include "sdf-indirect-receiver-certificate.hlsli"
#include "sdf-indirect-answer.hlsli"

static SdfIndirectSources sdfIndirectReceiverSources = (SdfIndirectSources)0;
static float3 sdfIndirectReceiverTotal = 0.0;
static bool sdfIndirectReceiverReplaced = false;
static uint sdfIndirectReceiverStatus = 0u;
static int sdfIndirectReceiverLevel = -1;
static uint sdfIndirectReceiverMask = 0u;
static float3 sdfIndirectReceiverPosition = 0.0;
static float3 sdfIndirectReceiverLaunch = 0.0;
static float sdfIndirectReceiverClearance = 0.0;
static float3 sdfIndirectReceiverNormal = 0.0;

void sdfIndirectPickBegin(SdfPixel p) {
    sdfIndirectReceiverStatus = passGroup.indirectTier == SdfIndirectTierOff ? 0u : 5u;
    sdfIndirectPickActive = p.active && passGroup.indirectPickPixel.z != 0u && all(p.pixel == passGroup.indirectPickPixel.xy);
    if (sdfIndirectPickActive) { sdfIndirectPickClearCorners(); }
}
// The receiver pass writes the near ray (words 59 and 68 to 70) and a replacing answer's sources.
void sdfIndirectPickFinish() {
    if (!sdfIndirectPickActive) { return; }
    sdfIndirectPickStore(0u, sdfIndirectReceiverStatus);
    sdfIndirectPickStore(1u, passGroup.indirectTier);
    sdfIndirectPickStore(2u, (uint)sdfIndirectReceiverLevel);
    sdfIndirectPickStore(3u, sdfIndirectReceiverMask);
    [unroll] for (uint lane = 0u; lane < 3u; lane++) {
        sdfIndirectPickStore(4u + lane, asuint(sdfIndirectReceiverPosition[lane]));
        sdfIndirectPickStore(8u + lane, asuint(sdfIndirectReceiverLaunch[lane]));
        sdfIndirectPickStore(12u + lane, asuint(sdfIndirectReceiverNormal[lane]));
    }
    sdfIndirectPickStore(7u, asuint(sdfIndirectReceiverClearance));
    sdfIndirectPickStore(11u, passGroup.indirectReadGeneration);
    sdfIndirectPickStore(15u, passGroup.indirectReadPublication);
    bool sources = !(sdfIndirectReceiverReplaced && (sdfIndirectReceiverStatus == 3u));
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        if (sources) {
            [unroll] for (uint lane = 0u; lane < 3u; lane++) {
                sdfIndirectPickStore(48u + 4u * source + lane, asuint(sdfIndirectReceiverSources.values[source][lane]));
            }
        }
    }
    sdfIndirectPickStore(51u, passGroup.indirectMethod);
    sdfIndirectPickStore(55u, passGroup.indirectSources);
    sdfIndirectPickStore(63u, 0u);
    sdfIndirectPickStore(67u, 0u);
    sdfIndirectPickStore(71u, passGroup.indirectPreviousPublication);
    puckCountDetail(SDF_SKY_DETAIL_INDIRECT, 0u, sdfIndirectPickStores, 0u, 0u, 0u);
    if (passGroup.workCounterRowDetail == 0u) {
        puckAddWork(passGroup.workCounterRow * PuckWorkRowWords + PuckWorkTexelsWord, sdfIndirectPickStores);
    }
}

// The pixel's indirect light, each enabled source summed in source order: the receiver's replacing answer, or the
// complete bank's irradiance at the certified launch about the shading normal. A bank read that finds no resolved
// texel leaves the pixel unresolved (status 2), as a retained certificate's failed read always did.
float3 sdfIndirectApply(SdfPixel p, SdfSurfaceSample receiver, float3 surfacePoint, float3 normal) {
    uint beforeLoads = sdfIndirectLoads;
    sdfIndirectReceiverPosition = surfacePoint;
    sdfIndirectReceiverNormal = normal;
    float3 total = 0.0;
    if (passGroup.indirectTier != SdfIndirectTierOff) {
        uint count, stride;
        indirectAnswer.GetDimensions(count, stride);
        uint index = sdfIndirectAnswerIndex(p.pixel, p.viewIndex);
        if (index < count) {
            uint4 answer = indirectAnswer[index];
            sdfIndirectReceiverStatus = answer.x & SdfIndirectAnswerStatusMask;
            sdfIndirectReceiverReplaced = (answer.x & SdfIndirectAnswerReplaced) != 0u;
            sdfEvalCount += (float)(answer.x >> SdfIndirectAnswerEvaluationShift);
            if (sdfIndirectReceiverStatus == 3u) {
                uint level;
                uint mask;
                float3 launched;
                float clearance;
                SdfIndirectSources cache;
                if (sdfIndirectReceiverCertificate(worldVisibilityRecord(p.pixel, p.viewIndex), level, mask, launched, clearance)
                    && sdfIndirectIrradianceAt(surfacePoint, launched, normal, level, mask,
                        passGroup.indirectReadGeneration, passGroup.indirectReadPublication, cache)) {
                    sdfIndirectReceiverLevel = (int)level;
                    sdfIndirectReceiverMask = mask;
                    sdfIndirectReceiverLaunch = launched;
                    sdfIndirectReceiverClearance = clearance;
                    if (sdfIndirectReceiverReplaced) {
                        total = asfloat(answer.yzw);
                    } else {
                        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
                            if ((passGroup.indirectSources & (1u << source)) == 0u) { cache.values[source] = 0.0; }
                        }
                        sdfIndirectReceiverSources = cache;
                        total = sdfIndirectSourceTotal(cache);
                    }
                } else {
                    sdfIndirectReceiverStatus = 2u;
                }
            }
        }
    }
    sdfIndirectReceiverTotal = total;
    puckCountIndirect(SDF_SKY_DETAIL_INDIRECT, 0u, sdfIndirectLoads - beforeLoads,
        sdfIndirectReceiverStatus == 2u || sdfIndirectReceiverStatus == 4u ? 1u : 0u);
    return total;
}
#endif
