// A High receiver replaces one incoming cosine sample; resolved colour and temporal history never supply radiance.
#ifndef SDF_INDIRECT_NEAR_HLSLI
#define SDF_INDIRECT_NEAR_HLSLI
#include "sdf-indirect-near-policy.hlsli"
#include "sdf-indirect-near-incoming.hlsli"
#include "sdf-indirect-alternatives.hlsli"

static uint sdfIndirectNearOutcome = SdfIndirectNearOutcomeNotAttempted;
static float3 sdfIndirectNearDirection = 0.0;

#ifdef __spirv__
[noinline]
#endif
SdfIndirectSources sdfIndirectNear(SdfPixel p, SdfSurfaceSample receiver, float3 launched,
    SdfIndirectSources fallback, out uint countedLoads) {
    countedLoads = 0u;
    if (passGroup.indirectNearEnabled == 0u || !sdfIndirectNearAdmitted(passGroup.indirectTier, passGroup.indirectMethod, p.pixel, passGroup.historyFrames)) { return fallback; }
    // The selected pixel sees every radial stratum over four visits, rather than the same ray on every fourth frame.
    uint visit = passGroup.historyFrames / SdfIndirectNearPhases;
    float3 direction = sdfIndirectAlternativeDirection(receiver.normal, visit % SdfIndirectAlternativeRays,
        (visit / SdfIndirectAlternativeRays) % SdfIndirectAlternativePhases);
    sdfIndirectNearDirection = direction;
    bool previousSecondary = sdfSecondaryMarchActive;
    bool previousShadow = sdfShadowParticipationActive;
    bool previousPermit = sdfIndirectReceiverPermit;
    bool previousDeferred = sdfIndirectReceiverDeferred;
    bool previousPick = sdfIndirectPickActive;
    // Secondary feedback reads must not replace the primary receiver's corner provenance.
    sdfIndirectPickActive = false;
    sdfSecondaryMarchActive = true;
    sdfShadowParticipationActive = true;
    uint beforeLoads = sdfIndirectLoads;
    uint beforeHashes = sdfIndirectHashes;
    SdfIndirectSources incoming;
    bool hitSurface;
    uint evaluations;
    bool answered = sdfIndirectNearIncoming(launched, direction, incoming, hitSurface, evaluations);
    sdfIndirectPickActive = previousPick;
    sdfIndirectReceiverDeferred = previousDeferred;
    sdfIndirectReceiverPermit = previousPermit;
    sdfShadowParticipationActive = previousShadow;
    sdfSecondaryMarchActive = previousSecondary;
    sdfIndirectNearOutcome = answered ? (hitSurface ? SdfIndirectNearOutcomeHit : SdfIndirectNearOutcomeContinuation) : SdfIndirectNearOutcomeUnresolved;
    countedLoads = sdfIndirectLoads - beforeLoads;
    puckCountDetail(SDF_SKY_DETAIL_INDIRECT_NEAR, evaluations, 0u, 0u, sdfIndirectHashes - beforeHashes, countedLoads);
    puckCountIndirect(SDF_SKY_DETAIL_INDIRECT_NEAR, answered && hitSurface ? 1u : 0u, 1u, answered ? 0u : 1u);
    if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps -= evaluations; }
    return sdfIndirectNearResult(answered, incoming, fallback);
}
#endif
