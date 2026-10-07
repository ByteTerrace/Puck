// Receiver proofs use the same complete-field certificate as transport; shading reads only a complete lighting bank.
#ifndef SDF_INDIRECT_APPLY_HLSLI
#define SDF_INDIRECT_APPLY_HLSLI
#include "sdf-indirect-irradiance.hlsli"
#include "sdf-indirect-approach.hlsli"
#include "../isa/sdf-sky-kinds.hlsli"
#include "sdf-indirect-near.hlsli"

static SdfIndirectSources sdfIndirectReceiverSources = (SdfIndirectSources)0;
static uint sdfIndirectReceiverStatus = 0u;
static int sdfIndirectReceiverLevel = -1;
static uint sdfIndirectReceiverMask = 0u;
static float3 sdfIndirectReceiverPosition = 0.0;
static float3 sdfIndirectReceiverLaunch = 0.0;
static float sdfIndirectReceiverClearance = 0.0;
static float3 sdfIndirectReceiverNormal = 0.0;

#include "sdf-indirect-receiver-certificate.hlsli"

void sdfIndirectPickBegin(SdfPixel p) {
    sdfIndirectNearOutcome = SdfIndirectNearOutcomeNotAttempted;
    sdfIndirectNearDirection = 0.0;
    sdfIndirectReceiverStatus = passGroup.indirectTier == SdfIndirectTierOff ? 0u : 5u;
    sdfIndirectPickActive = p.active && passGroup.indirectPickPixel.z != 0u && all(p.pixel == passGroup.indirectPickPixel.xy);
    if (sdfIndirectPickActive) { sdfIndirectPickClearCorners(); }
}
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
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        [unroll] for (uint lane = 0u; lane < 3u; lane++) {
            sdfIndirectPickStore(48u + 4u * source + lane, asuint(sdfIndirectReceiverSources.values[source][lane]));
        }
        sdfIndirectPickStore(51u + 4u * source, 0u);
    }
    sdfIndirectPickStore(51u, passGroup.indirectMethod);
    sdfIndirectPickStore(55u, passGroup.indirectSources);
    sdfIndirectPickStore(59u, sdfIndirectNearOutcome);
    [unroll] for (uint lane = 0u; lane < 3u; lane++) {
        sdfIndirectPickStore(68u + lane, asuint(sdfIndirectNearDirection[lane]));
    }
    sdfIndirectPickStore(71u, passGroup.indirectPreviousPublication);
    puckCountDetail(SDF_SKY_DETAIL_INDIRECT, 0u, sdfIndirectPickStores, 0u, 0u, 0u);
    if (passGroup.workCounterRowDetail == 0u) {
        puckAddWork(passGroup.workCounterRow * PuckWorkRowWords + PuckWorkTexelsWord, sdfIndirectPickStores);
    }
}

SdfIndirectSources sdfIndirectReceiver(SdfPixel p, SdfSurfaceSample receiver, float3 surfacePoint, float3 normal) {
    SdfIndirectSources result = (SdfIndirectSources)0;
    sdfIndirectReceiverPosition = surfacePoint;
    sdfIndirectReceiverNormal = normal;
    if (passGroup.indirectTier == SdfIndirectTierOff) { return result; }
    uint record = worldVisibilityRecord(p.pixel, p.viewIndex);
    if (!sdfIndirectReceiverRecordValid(record)) { return result; }
    uint source = sdfVisibilitySource(sdfLoadVisibility(record).identity);
    uint policy = SDF_INDIRECT_PARTICIPATION_CAST;
    if (receiver.mesh) {
        uint meshWords, meshStride;
        sdfMeshRegion.GetDimensions(meshWords, meshStride);
        if (source >= meshWords / SdfMeshDrawWords) { return result; }
        uint flags = sdfMeshRegion[sdfMeshRecord(source) + SdfMeshFlagsWord];
        policy = sdfIndirectPolicy((flags & SDF_INSTANCE_INDIRECT_MASK) >> SDF_INSTANCE_INDIRECT_SHIFT, (flags & SdfMeshDynamicFlag) != 0u);
    } else if (source != 0u) {
        if (!sdfProgramLayout.valid || source > sdfProgramLayout.instanceCount) { return result; }
        policy = sdfInstanceIndirectPolicy(sdfWords[sdfInstanceEntryOffset(sdfProgramLayout.instanceOffset, source - 1u) + 1u]);
    }
    if (policy == SDF_INDIRECT_PARTICIPATION_OFF) { sdfIndirectReceiverStatus = 0u; return result; }
    // Demand counts real receivers even before the cache publishes its first lighting bank.
    uint receiverWords, receiverStride;
    indirectDeferredRW.GetDimensions(receiverWords, receiverStride);
    if (receiverWords > 1u) {
        uint ignored;
        InterlockedAdd(indirectDeferredRW[1], 1u, ignored);
    }
    sdfIndirectReceiverStatus = 1u;
    if (passGroup.indirectReadPublication == 0u) { return result; }
    sdfIndirectReceiverStatus = 2u;
    uint retainedLevel;
    uint retainedMask;
    float3 retainedLaunch;
    float retainedClearance;
    if (sdfIndirectReceiverCertificate(record, retainedLevel, retainedMask, retainedLaunch, retainedClearance)) {
        if (retainedMask != 0u && sdfIndirectIrradianceAt(surfacePoint, retainedLaunch, normal, retainedLevel, retainedMask,
            passGroup.indirectReadGeneration, passGroup.indirectReadPublication, result)) {
            sdfIndirectReceiverLevel = (int)retainedLevel;
            sdfIndirectReceiverMask = retainedMask;
            sdfIndirectReceiverLaunch = retainedLaunch;
            sdfIndirectReceiverClearance = retainedClearance;
            sdfIndirectReceiverStatus = 3u;
            return result;
        }
        return result;
    }
    uint approach = receiver.mesh ? 0u : sdfVisibilityApproach(record);
    float approachClearance = sdfIndirectApproachClearance(approach);
    [loop] for (uint level = 0u; level < sdfIndirectLevelCount(); level++) {
        float spacing = sdfIndirectSpacing(passGroup.indirectTier, level);
        float3 launched = sdfIndirectApproachPoint(surfacePoint, p.rayDirection, approach);
        float clearance = approachClearance;
        if (!(clearance > 0.0)) {
            // A mesh or grazing primary has no approach certificate. Its normal launch shares the same finite
            // admission as the following proof, so invisible or deferred receivers do no unbounded field work.
            if (!sdfIndirectAdmitReceiver()) { break; }
            sdfIndirectReceiverPermit = true;
            uint launchBudget = SdfIndirectLaunchSteps;
            if (!sdfIndirectLaunch(surfacePoint, receiver.normal, spacing, launchBudget, launched, clearance)) {
                sdfIndirectReceiverPermit = false;
                continue;
            }
        }
        uint proofBudget = 2u * SdfIndirectSegmentSteps;
        uint mask = sdfIndirectProve(launched, level, proofBudget, clearance);
        sdfIndirectReceiverPermit = false;
        if (sdfIndirectReceiverDeferred) { break; }
        if (mask == 0u || !sdfIndirectIrradianceAt(surfacePoint, launched, normal, level, mask,
            passGroup.indirectReadGeneration, passGroup.indirectReadPublication, result)) { continue; }
        sdfIndirectReceiverLevel = (int)level;
        sdfIndirectReceiverMask = mask;
        sdfIndirectReceiverLaunch = launched;
        sdfIndirectReceiverClearance = clearance;
        sdfIndirectReceiverStatus = 3u;
        sdfIndirectStoreReceiverCertificate(record, level, mask, launched, clearance, true);
        return result;
    }
    if (sdfIndirectReceiverDeferred) {
        sdfIndirectReceiverStatus = 4u;
        if (passGroup.indirectReceiverProofs != 0u) {
            uint deferredWords, deferredStride;
            indirectDeferredRW.GetDimensions(deferredWords, deferredStride);
            uint ignored;
            if (deferredWords != 0u) { InterlockedAdd(indirectDeferredRW[0], 1u, ignored); }
        }
    }
    sdfIndirectStoreReceiverCertificate(record, 0u, 0u, 0.0, 0.0, !sdfIndirectReceiverDeferred);
    return result;
}

SdfIndirectSources sdfIndirectApply(SdfPixel p, SdfSurfaceSample receiver, float3 surfacePoint, float3 normal) {
    uint beforeSteps = sdfWorkSteps;
    uint beforeQueries = sdfIndirectEvaluations;
    uint beforeLoads = sdfIndirectLoads;
    SdfIndirectSources cacheSources = sdfIndirectReceiver(p, receiver, surfacePoint, normal);
    SdfIndirectSources selectedSources = cacheSources;
    if (sdfIndirectReceiverStatus == 3u) {
        selectedSources = sdfIndirectAlternative(p, receiver, sdfIndirectReceiverLaunch, cacheSources);
        uint nearLoads;
        selectedSources = sdfIndirectNear(p, receiver, sdfIndirectReceiverLaunch, selectedSources, nearLoads);
        beforeLoads += nearLoads;
    }
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        if ((passGroup.indirectSources & (1u << source)) == 0u) { selectedSources.values[source] = 0.0; }
    }
    sdfIndirectReceiverSources = selectedSources;
    uint steps = sdfWorkSteps - beforeSteps;
    sdfEvalCount += (float)(sdfIndirectEvaluations - beforeQueries);
    puckCountDetail(SDF_SKY_DETAIL_INDIRECT, steps, 0u, 0u, 0u, 0u);
    puckCountIndirect(SDF_SKY_DETAIL_INDIRECT, 0u, sdfIndirectLoads - beforeLoads,
        sdfIndirectReceiverStatus == 2u || sdfIndirectReceiverStatus == 4u ? 1u : 0u);
    if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps -= steps; }
    return sdfIndirectReceiverSources;
}
#endif
