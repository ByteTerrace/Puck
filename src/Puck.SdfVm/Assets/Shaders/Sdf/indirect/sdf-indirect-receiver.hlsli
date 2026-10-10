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
// supported level, 3 a certified level, 4 a deferred proof and 5 a receiver outside the material path. A field receiver
// first marches its approach (sdf-indirect-approach.hlsli): a fixed number of complete-field samples per pixel, outside
// the shared admission, so a published canonical proof answers it with no admitted work. A mesh receiver, or one whose
// approach joins no sample, launches along its normal under that admission. A procedure (sdfIndirectReceiveStep): each
// approach sample is a query, each level's launch and proof a call, which its owner's run answers.
struct SdfIndirectReceiveProc {
    uint2 pixel;
    uint viewIndex;
    float3 rayDirection;
    SdfSurfaceSample receiver;
    float3 surfacePoint;
    uint record;
    float3 approachStart;
    float approachDistance;
    float surfaceClearance;
    uint approachStep;
    float approachLaunch;
    float3 approach;
    float approachClearance;
    uint level;
    float3 launched;
    float clearance;
    uint phase;
};
static SdfIndirectReceiveProc sdfIndirectReceiveProc = (SdfIndirectReceiveProc)0;

static const uint SdfIndirectReceiveBegun = 0u;
static const uint SdfIndirectReceiveLaunched = 1u;
static const uint SdfIndirectReceiveProved = 2u;
static const uint SdfIndirectReceiveSurface = 3u;
static const uint SdfIndirectReceiveApproach = 4u;

uint sdfIndirectReceiveBegin(SdfPixel p, SdfSurfaceSample receiver, float3 surfacePoint) {
    sdfIndirectReceiveProc.pixel = p.pixel;
    sdfIndirectReceiveProc.viewIndex = p.viewIndex;
    sdfIndirectReceiveProc.rayDirection = p.rayDirection;
    sdfIndirectReceiveProc.receiver = receiver;
    sdfIndirectReceiveProc.surfacePoint = surfacePoint;
    sdfIndirectReceiveProc.phase = SdfIndirectReceiveBegun;
    return SdfIndirectProcReceive;
}

// The certificate a receiver stores when no level certifies it: none, or deferred to a later frame's proof.
void sdfIndirectReceiveUncertified(uint record) {
    if (sdfIndirectReceiverDeferred) {
        sdfIndirectReceiverStatus = 4u;
        if (passGroup.indirectReceiverProofs != 0u) {
            uint deferredWords, deferredStride;
            indirectDeferredRW.GetDimensions(deferredWords, deferredStride);
            uint ignored;
            if (deferredWords != 0u) { InterlockedAdd(indirectDeferredRW[0], 1u, ignored); }
        }
    }
    sdfIndirectStoreReceiverCertificate(record, 0u, 0u, sdfIndirectReceiveProc.surfacePoint, 0.0, !sdfIndirectReceiverDeferred);
}

uint sdfIndirectReceiveStep() {
    uint record = sdfIndirectReceiveProc.record;
    if (sdfIndirectReceiveProc.phase == SdfIndirectReceiveBegun) {
        if (passGroup.indirectTier == SdfIndirectTierOff) { return SdfIndirectStepReturn; }
        record = worldVisibilityRecord(sdfIndirectReceiveProc.pixel, sdfIndirectReceiveProc.viewIndex);
        sdfIndirectReceiveProc.record = record;
        if (!sdfIndirectReceiverRecordValid(record)) { return SdfIndirectStepReturn; }
        uint source = sdfVisibilitySource(sdfLoadVisibility(record).identity);
        uint policy = SDF_INDIRECT_PARTICIPATION_CAST;
        if (sdfIndirectReceiveProc.receiver.mesh) {
            uint meshWords, meshStride;
            sdfMeshRegion.GetDimensions(meshWords, meshStride);
            if (source >= meshWords / SdfMeshDrawWords) { return SdfIndirectStepReturn; }
            uint flags = sdfMeshRegion[sdfMeshRecord(source) + SdfMeshFlagsWord];
            policy = sdfIndirectPolicy((flags & SDF_INSTANCE_INDIRECT_MASK) >> SDF_INSTANCE_INDIRECT_SHIFT, (flags & SdfMeshDynamicFlag) != 0u);
        } else if (source != 0u) {
            if (!sdfProgramLayout.valid || source > sdfProgramLayout.instanceCount) { return SdfIndirectStepReturn; }
            policy = sdfInstanceIndirectPolicy(sdfWords[sdfInstanceEntryOffset(sdfProgramLayout.instanceOffset, source - 1u) + 1u]);
        }
        if (policy == SDF_INDIRECT_PARTICIPATION_OFF) { sdfIndirectReceiverStatus = 0u; return SdfIndirectStepReturn; }
        // Demand counts real receivers even before the cache publishes its first lighting bank.
        uint receiverWords, receiverStride;
        indirectDeferredRW.GetDimensions(receiverWords, receiverStride);
        if (receiverWords > 1u) {
            uint ignored;
            InterlockedAdd(indirectDeferredRW[1], 1u, ignored);
        }
        sdfIndirectReceiverStatus = 1u;
        if (passGroup.indirectReadPublication == 0u) { return SdfIndirectStepReturn; }
        sdfIndirectReceiverStatus = 2u;
        uint retainedLevel;
        uint retainedMask;
        float3 retainedLaunch;
        float retainedClearance;
        if (sdfIndirectReceiverCertificate(record, retainedLevel, retainedMask, retainedLaunch, retainedClearance)) {
            if (sdfIndirectCertificateJoins(sdfIndirectReceiveProc.surfacePoint, retainedLaunch, retainedClearance,
                sdfIndirectReceiveProc.receiver.threshold)) {
                if (retainedMask != 0u && sdfIndirectIrradianceSupported(retainedLaunch, retainedLevel, retainedMask,
                    passGroup.indirectReadGeneration, passGroup.indirectReadPublication)) {
                    sdfIndirectReceiverLevel = retainedLevel;
                    sdfIndirectReceiverMask = retainedMask;
                    sdfIndirectReceiverLaunch = retainedLaunch;
                    sdfIndirectReceiverStatus = 3u;
                }
                return SdfIndirectStepReturn;
            }
            sdfIndirectWithdrawReceiverCertificate(record);
        }
        sdfIndirectReceiveProc.approach = sdfIndirectReceiveProc.surfacePoint;
        sdfIndirectReceiveProc.approachClearance = 0.0;
        sdfIndirectReceiveProc.level = 0u;
        if (!sdfIndirectReceiveProc.receiver.mesh) {
            sdfIndirectReceiveProc.approachStep = 0u;
            sdfIndirectReceiveProc.phase = SdfIndirectReceiveSurface;
            sdfIndirectApproachBefore = sdfFieldVisits;
            return sdfIndirectAsk(sdfIndirectReceiveProc.surfacePoint, SDF_INSTANCE_MASK_ALL);
        }
    } else if (sdfIndirectReceiveProc.phase == SdfIndirectReceiveSurface || sdfIndirectReceiveProc.phase == SdfIndirectReceiveApproach) {
        // The approach's samples are per-pixel work outside the shared allowance; its measured price excludes them.
        sdfIndirectApproachVisits = sdfFieldVisits - sdfIndirectApproachBefore;
        float ball = sdfMapBallClearance(sdfIndirectReply.distance);
        float spacing = sdfIndirectSpacing(passGroup.indirectTier, 0u);
        float3 direction = sdfIndirectReceiveProc.rayDirection;
        bool marching = false;
        if (sdfIndirectReceiveProc.phase == SdfIndirectReceiveSurface) {
            // surfaceClearance holds the surface point's slack; the launch is the chain's first sample.
            sdfIndirectReceiveProc.surfaceClearance = sdfIndirectApproachSlack(ball, sdfIndirectReceiveProc.receiver.threshold);
            precise float3 retreat = direction * (spacing * SdfIndirectApproachRetreat);
            precise float3 start = sdfIndirectReceiveProc.surfacePoint - retreat;
            sdfIndirectReceiveProc.approachStart = start;
            sdfIndirectReceiveProc.approachDistance = 0.0;
            marching = sdfIndirectReceiveProc.surfaceClearance >= 0.0 && all(isfinite(start));
        } else if (ball > 0.0 && isfinite(ball)) {
            float3 sample = sdfIndirectPointAt(sdfIndirectReceiveProc.approachStart, direction, sdfIndirectReceiveProc.approachDistance);
            if (sdfIndirectReceiveProc.approachStep == 1u) { sdfIndirectReceiveProc.approachLaunch = ball; }
            if (sdfIndirectApproachJoined(sdfIndirectReceiveProc.surfacePoint, sample, ball, sdfIndirectReceiveProc.surfaceClearance)) {
                sdfIndirectReceiveProc.approach = sdfIndirectReceiveProc.approachStart;
                sdfIndirectReceiveProc.approachClearance = sdfIndirectReceiveProc.approachLaunch;
            } else {
                // Each step lands on its sample's ball, so consecutive balls overlap and the chain stays connected.
                sdfIndirectReceiveProc.approachDistance += ball;
                marching = true;
            }
        }
        sdfIndirectReceiveProc.approachStep++;
        if (marching && sdfIndirectReceiveProc.approachStep <= SdfIndirectApproachSteps) {
            sdfIndirectReceiveProc.phase = SdfIndirectReceiveApproach;
            return sdfIndirectAsk(sdfIndirectPointAt(sdfIndirectReceiveProc.approachStart, direction, sdfIndirectReceiveProc.approachDistance), SDF_INSTANCE_MASK_ALL);
        }
    } else if (sdfIndirectReceiveProc.phase == SdfIndirectReceiveLaunched) {
        sdfIndirectReceiveProc.launched = sdfIndirectLaunchProc.position;
        sdfIndirectReceiveProc.clearance = sdfIndirectLaunchProc.clearance;
        if (!sdfIndirectLaunchProc.launched) {
            sdfIndirectReceiverPermit = false;
            sdfIndirectReceiveProc.level++;
        } else {
            sdfIndirectReceiveProc.phase = SdfIndirectReceiveProved;
            return sdfIndirectCall(sdfIndirectProveBegin(sdfIndirectReceiveProc.launched, sdfIndirectReceiveProc.level, SdfIndirectReceiverProofSteps, sdfIndirectReceiveProc.clearance, true));
        }
    } else {
        uint mask = sdfIndirectProveProc.mask;
        uint level = sdfIndirectReceiveProc.level;
        float3 launched = sdfIndirectReceiveProc.launched;
        sdfIndirectReceiverPermit = false;
        if (sdfIndirectReceiverDeferred) {
            sdfIndirectReceiveUncertified(record);
            return SdfIndirectStepReturn;
        }
        if (mask == 0u || !sdfIndirectIrradianceSupported(launched, level, mask,
            passGroup.indirectReadGeneration, passGroup.indirectReadPublication)) {
            sdfIndirectReceiveProc.level++;
        } else {
            sdfIndirectReceiverLevel = level;
            sdfIndirectReceiverMask = mask;
            sdfIndirectReceiverLaunch = launched;
            sdfIndirectReceiverStatus = 3u;
            sdfIndirectStoreReceiverCertificate(record, level, mask, launched, sdfIndirectReceiveProc.clearance, true);
            return SdfIndirectStepReturn;
        }
    }
    if (sdfIndirectReceiveProc.level < sdfIndirectLevelCount()) {
        uint level = sdfIndirectReceiveProc.level;
        float spacing = sdfIndirectSpacing(passGroup.indirectTier, level);
        float3 surfacePoint = sdfIndirectReceiveProc.surfacePoint;
        sdfIndirectReceiveProc.launched = sdfIndirectReceiveProc.approach;
        sdfIndirectReceiveProc.clearance = sdfIndirectReceiveProc.approachClearance;
        if (!(sdfIndirectReceiveProc.clearance > 0.0)) {
            // A mesh, or a receiver whose approach joined no sample, launches along its normal. That launch shares the
            // same finite admission as the following proof, so deferred receivers do no unbounded field work.
            if (sdfIndirectAdmitReceiver()) {
                sdfIndirectReceiverPermit = true;
                sdfIndirectReceiveProc.phase = SdfIndirectReceiveLaunched;
                return sdfIndirectCall(sdfIndirectLaunchBegin(surfacePoint, sdfIndirectReceiveProc.receiver.normal, spacing, SdfIndirectLaunchSteps));
            }
        } else {
            sdfIndirectReceiveProc.phase = SdfIndirectReceiveProved;
            return sdfIndirectCall(sdfIndirectProveBegin(sdfIndirectReceiveProc.launched, level, SdfIndirectReceiverProofSteps, sdfIndirectReceiveProc.clearance, true));
        }
    }
    sdfIndirectReceiveUncertified(record);
    return SdfIndirectStepReturn;
}

// The receiver pass's field work for one shaded pixel: its proof (sdfIndirectReceiveStep), then the replacement of its
// incoming light. The default receiver serves the cache method with its near-field sample; the comparison receiver
// folds the comparison methods over the cache answer read with the receiver's geometric normal. The kernel runs this
// one procedure, so every field query of the pass reaches its one interpreter call site.
struct SdfIndirectReceiverProc {
    SdfPixel p;
    SdfSurfaceSample receiver;
    float3 surfacePoint;
    SdfIndirectSources selected;
    bool replaced;
    uint nearLoads;
    uint beforeVisits;
#ifndef SDF_INDIRECT_COMPARISON
    bool previousSecondary;
    bool previousShadow;
    bool previousPermit;
    bool previousDeferred;
    bool previousPick;
    uint beforeLoads;
    uint beforeHashes;
#endif
    uint phase;
};
static SdfIndirectReceiverProc sdfIndirectReceiverProc = (SdfIndirectReceiverProc)0;

static const uint SdfIndirectReceiverBegun = 0u;
static const uint SdfIndirectReceiverReceived = 1u;
static const uint SdfIndirectReceiverReplaced = 2u;

uint sdfIndirectReceiverBegin(SdfPixel p, SdfSurfaceSample receiver, float3 surfacePoint) {
    sdfIndirectReceiverProc.p = p;
    sdfIndirectReceiverProc.receiver = receiver;
    sdfIndirectReceiverProc.surfacePoint = surfacePoint;
    sdfIndirectReceiverProc.phase = SdfIndirectReceiverBegun;
    return SdfIndirectProcReceiver;
}

uint sdfIndirectReceiverStep() {
    if (sdfIndirectReceiverProc.phase == SdfIndirectReceiverBegun) {
        sdfIndirectReceiverProc.selected = (SdfIndirectSources)0;
        sdfIndirectReceiverProc.replaced = false;
        sdfIndirectReceiverProc.nearLoads = 0u;
        sdfIndirectReceiverProc.beforeVisits = sdfFieldVisits;
        sdfIndirectApproachVisits = 0u;
        sdfIndirectReceiverProc.phase = SdfIndirectReceiverReceived;
        return sdfIndirectCall(sdfIndirectReceiveBegin(sdfIndirectReceiverProc.p, sdfIndirectReceiverProc.receiver, sdfIndirectReceiverProc.surfacePoint));
    }
    SdfPixel p = sdfIndirectReceiverProc.p;
    if (sdfIndirectReceiverProc.phase == SdfIndirectReceiverReceived) {
        // The admitted receiver's launch and proof are the work its shared allowance prices; near and comparison
        // samples carry their own per-pixel limits.
        sdfIndirectReceiverVisits = sdfFieldVisits - sdfIndirectReceiverProc.beforeVisits - sdfIndirectApproachVisits;
        if (sdfIndirectReceiverStatus != 3u) { return SdfIndirectStepReturn; }
#ifdef SDF_INDIRECT_COMPARISON
        // The comparison receiver: near-field samples are admitted only with the cache method, so it carries none.
        if (!sdfIndirectAlternativeAdmitted(p)) { return SdfIndirectStepReturn; }
        SdfIndirectSources fallback;
        sdfIndirectIrradianceAt(sdfIndirectReceiverProc.surfacePoint, sdfIndirectReceiverLaunch, sdfIndirectReceiverProc.receiver.normal,
            sdfIndirectReceiverLevel, sdfIndirectReceiverMask, passGroup.indirectReadGeneration, passGroup.indirectReadPublication, fallback);
        sdfIndirectReceiverProc.phase = SdfIndirectReceiverReplaced;
        return sdfIndirectCall(sdfIndirectAlternativeBegin(p, sdfIndirectReceiverProc.receiver.normal, sdfIndirectReceiverLaunch, fallback));
#else
        // The default receiver serves the cache method; a comparison method is the comparison receiver's. The near
        // sample replaces the cache answer (sdfIndirectNearResult) when answered; zero, unanswered, when not admitted
        // or unresolved, keeping every cache source.
        if (passGroup.indirectNearEnabled == 0u || !sdfIndirectNearAdmitted(passGroup.indirectTier, passGroup.indirectMethod, p.pixel, passGroup.historyFrames)) { return SdfIndirectStepReturn; }
        // The selected pixel sees every radial stratum over four visits, rather than the same ray on every fourth frame.
        uint visit = passGroup.historyFrames / SdfIndirectNearPhases;
        float3 direction = sdfIndirectAlternativeDirection(sdfIndirectReceiverProc.receiver.normal, visit % SdfIndirectAlternativeRays,
            (visit / SdfIndirectAlternativeRays) % SdfIndirectAlternativePhases);
        sdfIndirectNearDirection = direction;
        sdfIndirectReceiverProc.previousSecondary = sdfSecondaryMarchActive;
        sdfIndirectReceiverProc.previousShadow = sdfShadowParticipationActive;
        sdfIndirectReceiverProc.previousPermit = sdfIndirectReceiverPermit;
        sdfIndirectReceiverProc.previousDeferred = sdfIndirectReceiverDeferred;
        sdfIndirectReceiverProc.previousPick = sdfIndirectPickActive;
        // Secondary feedback reads must not replace the primary receiver's corner provenance.
        sdfIndirectPickActive = false;
        sdfSecondaryMarchActive = true;
        sdfShadowParticipationActive = true;
        sdfIndirectReceiverProc.beforeLoads = sdfIndirectLoads;
        sdfIndirectReceiverProc.beforeHashes = sdfIndirectHashes;
        sdfIndirectReceiverProc.phase = SdfIndirectReceiverReplaced;
        return sdfIndirectCall(sdfIndirectNearIncomingBegin(sdfIndirectReceiverLaunch, direction));
#endif
    }
#ifdef SDF_INDIRECT_COMPARISON
    sdfIndirectReceiverProc.selected = sdfIndirectAlternativeProc.total;
    sdfIndirectReceiverProc.replaced = true;
#else
    bool answered = sdfIndirectNearIncomingProc.answered;
    bool hitSurface = sdfIndirectNearIncomingProc.hitSurface;
    uint evaluations = sdfIndirectNearIncomingProc.evaluations;
    sdfIndirectPickActive = sdfIndirectReceiverProc.previousPick;
    sdfIndirectReceiverDeferred = sdfIndirectReceiverProc.previousDeferred;
    sdfIndirectReceiverPermit = sdfIndirectReceiverProc.previousPermit;
    sdfShadowParticipationActive = sdfIndirectReceiverProc.previousShadow;
    sdfSecondaryMarchActive = sdfIndirectReceiverProc.previousSecondary;
    sdfIndirectNearOutcome = answered ? (hitSurface ? SdfIndirectNearOutcomeHit : SdfIndirectNearOutcomeContinuation) : SdfIndirectNearOutcomeUnresolved;
    uint countedLoads = sdfIndirectLoads - sdfIndirectReceiverProc.beforeLoads;
    sdfIndirectReceiverProc.nearLoads = countedLoads;
    puckCountDetail(SDF_SKY_DETAIL_INDIRECT_NEAR, evaluations, 0u, 0u, sdfIndirectHashes - sdfIndirectReceiverProc.beforeHashes, countedLoads);
    puckCountIndirect(SDF_SKY_DETAIL_INDIRECT_NEAR, answered && hitSurface ? 1u : 0u, 1u, answered ? 0u : 1u);
    if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps -= evaluations; }
    if (answered) {
        sdfIndirectReceiverProc.selected = sdfIndirectNearIncomingProc.result;
        sdfIndirectReceiverProc.replaced = true;
    }
#endif
    return SdfIndirectStepReturn;
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
        sdfIndirectRun(sdfIndirectReceiverBegin(p, s, surfacePoint));
        selected = sdfIndirectReceiverProc.selected;
        replaced = sdfIndirectReceiverProc.replaced;
        beforeLoads += sdfIndirectReceiverProc.nearLoads;
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
    sdfIndirectReportCost(SdfIndirectCostReceiver, sdfIndirectReceiverVisits, sdfIndirectReceiverAdmitted ? 1u : 0u);
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
