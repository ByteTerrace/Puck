// The receiver pass: every field query a shaded pixel's indirect light needs, ahead of views. It proves the pixel's
// receiver against the cache with the same complete-field certificate as transport, publishes the certificate in the
// visibility record, and answers the near-field or comparison replacement of its incoming light (sdf-indirect-answer.hlsli).
// Views then reads the complete lighting bank at the certified launch with its shading normal and performs no field query.
#ifndef SDF_INDIRECT_RECEIVER_HLSLI
#define SDF_INDIRECT_RECEIVER_HLSLI
#ifdef SDF_RECEIVER_PASS
#include "sdf-indirect-irradiance.hlsli"
#include "sdf-indirect-approach.hlsli"
#include "../isa/sdf-sky-kinds.hlsli"
#include "sdf-indirect-near.hlsli"
#include "sdf-indirect-receiver-certificate.hlsli"
#include "sdf-indirect-answer.hlsli"

static uint sdfIndirectReceiverStatus = 0u;
static uint sdfIndirectReceiverLevel = 0u;
static uint sdfIndirectReceiverMask = 0u;
static float3 sdfIndirectReceiverLaunch = 0.0;

// sdfIndirectIrradianceAt's answer short of any shading normal: the level's cell is current and one of the proof's
// corners is published in this bank, active or relocated and of positive trilinear weight, where the facing term of
// every corner is positive. Views reads the irradiance with its shading normal; the octahedral texels it lands on can
// still be unresolved, which leaves that pixel unresolved.
bool sdfIndirectIrradianceSupported(float3 launched, uint level, uint mask, uint generation, uint publication) {
    if (level >= sdfIndirectLevelCount() || generation >= SdfIndirectLightingGenerations || publication == 0u) { return false; }
    int3 cell;
    if (!sdfIndirectCellAt(launched, sdfIndirectSpacing(passGroup.indirectTier, level), cell)) { return false; }
    float3 fraction = frac(launched / sdfIndirectSpacing(passGroup.indirectTier, level));
    if (!sdfIndirectCellCurrent(sdfIndirectProbeIndex(cell, level))) { return false; }
    [loop] for (uint corner = 0u; corner < 8u; corner++) {
        if ((mask & (1u << corner)) == 0u) { continue; }
        int index = sdfIndirectProbeIndex(cell + sdfIndirectCorner(corner), level);
        if (index < 0 || (uint)index >= sdfIndirectProbeCapacity(passGroup.indirectTier)) { continue; }
        if (sdfIndirectLoad(sdfIndirectPublicationAddress((uint)index, generation)) != publication) { continue; }
        SdfIndirectPlacement probe = sdfIndirectReadProbe(index);
        if (probe.classification != SdfIndirectClassActive && probe.classification != SdfIndirectClassRelocated) { continue; }
        if (sdfIndirectCornerWeight(fraction, corner) > 0.0) { return true; }
    }
    return false;
}

// The receiver's proof: the retained certificate when it still stands, or the finest level whose launch, proof and
// published support succeed, stored as the pixel's certificate. Status 0 is no receiver, 1 no published bank, 2 no
// supported level, 3 a certified level, 4 a deferred proof and 5 a receiver outside the material path.
void sdfIndirectReceive(SdfPixel p, SdfSurfaceSample receiver, float3 surfacePoint) {
    if (passGroup.indirectTier == SdfIndirectTierOff) { return; }
    uint record = worldVisibilityRecord(p.pixel, p.viewIndex);
    if (!sdfIndirectReceiverRecordValid(record)) { return; }
    uint source = sdfVisibilitySource(sdfLoadVisibility(record).identity);
    uint policy = SDF_INDIRECT_PARTICIPATION_CAST;
    if (receiver.mesh) {
        uint meshWords, meshStride;
        sdfMeshRegion.GetDimensions(meshWords, meshStride);
        if (source >= meshWords / SdfMeshDrawWords) { return; }
        uint flags = sdfMeshRegion[sdfMeshRecord(source) + SdfMeshFlagsWord];
        policy = sdfIndirectPolicy((flags & SDF_INSTANCE_INDIRECT_MASK) >> SDF_INSTANCE_INDIRECT_SHIFT, (flags & SdfMeshDynamicFlag) != 0u);
    } else if (source != 0u) {
        if (!sdfProgramLayout.valid || source > sdfProgramLayout.instanceCount) { return; }
        policy = sdfInstanceIndirectPolicy(sdfWords[sdfInstanceEntryOffset(sdfProgramLayout.instanceOffset, source - 1u) + 1u]);
    }
    if (policy == SDF_INDIRECT_PARTICIPATION_OFF) { sdfIndirectReceiverStatus = 0u; return; }
    // Demand counts real receivers even before the cache publishes its first lighting bank.
    uint receiverWords, receiverStride;
    indirectDeferredRW.GetDimensions(receiverWords, receiverStride);
    if (receiverWords > 1u) {
        uint ignored;
        InterlockedAdd(indirectDeferredRW[1], 1u, ignored);
    }
    sdfIndirectReceiverStatus = 1u;
    if (passGroup.indirectReadPublication == 0u) { return; }
    sdfIndirectReceiverStatus = 2u;
    uint retainedLevel;
    uint retainedMask;
    float3 retainedLaunch;
    float retainedClearance;
    if (sdfIndirectReceiverCertificate(record, retainedLevel, retainedMask, retainedLaunch, retainedClearance)) {
        if (retainedMask != 0u && sdfIndirectIrradianceSupported(retainedLaunch, retainedLevel, retainedMask,
            passGroup.indirectReadGeneration, passGroup.indirectReadPublication)) {
            sdfIndirectReceiverLevel = retainedLevel;
            sdfIndirectReceiverMask = retainedMask;
            sdfIndirectReceiverLaunch = retainedLaunch;
            sdfIndirectReceiverStatus = 3u;
        }
        return;
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
        if (mask == 0u || !sdfIndirectIrradianceSupported(launched, level, mask,
            passGroup.indirectReadGeneration, passGroup.indirectReadPublication)) { continue; }
        sdfIndirectReceiverLevel = level;
        sdfIndirectReceiverMask = mask;
        sdfIndirectReceiverLaunch = launched;
        sdfIndirectReceiverStatus = 3u;
        sdfIndirectStoreReceiverCertificate(record, level, mask, launched, clearance, true);
        return;
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
}

// The receiver stage: one shaded pixel's proof and replacement, published as its answer for views. The comparison
// methods fold over the cache answer read with the receiver's geometric normal; the cache answer views shades with
// reads its own shading normal.
void sdfReceiverStage(SdfPixel p) {
    sdfIndirectNearOutcome = SdfIndirectNearOutcomeNotAttempted;
    sdfIndirectNearDirection = 0.0;
    sdfIndirectReceiverStatus = passGroup.indirectTier == SdfIndirectTierOff ? 0u : 5u;
    sdfIndirectPickActive = p.active && passGroup.indirectPickPixel.z != 0u && all(p.pixel == passGroup.indirectPickPixel.xy);
    if (!p.active) { return; }
    SdfSurfaceSample s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));
    SdfIndirectSources selected = (SdfIndirectSources)0;
    bool replaced = false;
    if (sdfIndirectAnswers(p, s)) {
        uint beforeSteps = sdfWorkSteps;
        uint beforeQueries = sdfIndirectEvaluations;
        uint beforeLoads = sdfIndirectLoads;
        precise float3 rayTravel = p.rayDirection * s.t;
        precise float3 surfacePoint = p.rayOrigin + rayTravel;
        sdfIndirectReceive(p, s, surfacePoint);
        if (sdfIndirectReceiverStatus == 3u) {
#ifdef SDF_INDIRECT_COMPARISON
            // The comparison receiver: near-field samples are admitted only with the cache method, so it carries none.
            if (sdfIndirectAlternativeAdmitted(p)) {
                SdfIndirectSources fallback;
                sdfIndirectIrradianceAt(surfacePoint, sdfIndirectReceiverLaunch, s.normal, sdfIndirectReceiverLevel, sdfIndirectReceiverMask,
                    passGroup.indirectReadGeneration, passGroup.indirectReadPublication, fallback);
                selected = sdfIndirectAlternative(p, s, sdfIndirectReceiverLaunch, fallback);
                replaced = true;
            }
#else
            // The default receiver serves the cache method; a comparison method is the comparison receiver's.
            bool answered;
            uint nearLoads;
            SdfIndirectSources incoming = sdfIndirectNear(p, s, sdfIndirectReceiverLaunch, answered, nearLoads);
            beforeLoads += nearLoads;
            if (answered) {
                selected = incoming;
                replaced = true;
            }
#endif
        }
        float3 total = 0.0;
        if (replaced) {
            [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
                if ((passGroup.indirectSources & (1u << source)) == 0u) { selected.values[source] = 0.0; }
            }
            total = sdfIndirectSourceTotal(selected);
        }
        uint steps = sdfWorkSteps - beforeSteps;
        uint evaluations = min(sdfIndirectEvaluations - beforeQueries, SdfIndirectAnswerEvaluationLimit);
        puckCountDetail(SDF_SKY_DETAIL_INDIRECT, steps, 0u, 0u, 0u, 0u);
        puckCountIndirect(SDF_SKY_DETAIL_INDIRECT, 0u, sdfIndirectLoads - beforeLoads, 0u);
        if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps -= steps; }
        uint count, stride;
        indirectAnswerRW.GetDimensions(count, stride);
        uint index = sdfIndirectAnswerIndex(p.pixel, p.viewIndex);
        if (index < count) {
            indirectAnswerRW[index] = uint4(sdfIndirectReceiverStatus | (replaced ? SdfIndirectAnswerReplaced : 0u)
                | (evaluations << SdfIndirectAnswerEvaluationShift), asuint(total));
            sdfWorkTexels = 1u;
        }
    }
    if (!sdfIndirectPickActive) { return; }
    // Views writes the rest of the selected pixel's record; the near ray and a replacing answer's sources are the
    // receiver's.
    sdfIndirectPickStore(59u, sdfIndirectNearOutcome);
    [unroll] for (uint lane = 0u; lane < 3u; lane++) {
        sdfIndirectPickStore(68u + lane, asuint(sdfIndirectNearDirection[lane]));
    }
    if (replaced) {
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
            [unroll] for (uint lane = 0u; lane < 3u; lane++) {
                sdfIndirectPickStore(48u + 4u * source + lane, asuint(selected.values[source][lane]));
            }
        }
    }
    puckCountDetail(SDF_SKY_DETAIL_INDIRECT, 0u, sdfIndirectPickStores, 0u, 0u, 0u);
    if (passGroup.workCounterRowDetail == 0u) {
        puckAddWork(passGroup.workCounterRow * PuckWorkRowWords + PuckWorkTexelsWord, sdfIndirectPickStores);
    }
}
#endif
#endif
