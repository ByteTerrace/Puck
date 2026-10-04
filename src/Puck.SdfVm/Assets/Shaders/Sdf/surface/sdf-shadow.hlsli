// One rolled gather/march site serves each stable slot and each active incoming handoff. All lanes reach each gather
// and the barrier behind its march before any lane clears the shared mask for the next light.
#ifndef SURFACE_SDF_SHADOW_HLSLI
#define SURFACE_SDF_SHADOW_HLSLI
#ifdef SDF_SHADOW_PASS
#include "../frame/sdf-reprojection.hlsli"
#include "../../../../../Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli"

bool sdfShadowHistoryAt(uint record, SdfVisibility visibility, float3 currentPoint, uint viewIndex, out float4 history) {
    history = float4(1.0, 1.0, 1.0, 1.0);
    float2 previousPixel;
    float previousT;
    if ((passGroup.historyFrames == 0u) || !sdfReprojection(record, currentPoint, previousPixel, previousT)) { return false; }
    uint2 extent = (uint2)passGroup.previousView[4].xy;
    if (any(previousPixel < 0.0) || any(previousPixel >= (float2)extent)) { return false; }
    // Reconstruct K at the preceding sample grid's fractional position. Repeated nearest-pixel copies would carry
    // one old sample across the jitter sequence while each write gave it the current pixel's receiver record.
    PuckReconstructionFootprint footprint = puckReconstructionFootprintAt((previousPixel - 0.5), extent, 0.0);
    float4 taps[16];
    [unroll] for (uint tap = 0u; tap < 16u; tap++) { taps[tap] = float4(0.0, 0.0, 0.0, 0.0); }
    [unroll] for (uint corner = 0u; corner < 4u; corner++) {
        uint2 offset = uint2((corner & 1u), (corner >> 1u));
        float2 weight = lerp((1.0 - footprint.f), footprint.f, (float2)offset);
        if ((weight.x * weight.y) > 0.0) {
            uint2 pixel = (uint2)clamp((footprint.origin + (int2)offset), int2(0, 0), ((int2)extent - 1));
            uint word = (SDF_SHADOW_HISTORY_WORDS * (((viewIndex * extent.y + pixel.y) * extent.x) + pixel.x));
            // Every contributing tap must belong to the preceding writer and the same receiver. One invalid tap
            // rejects the reconstruction; no stale or other-surface K is blended into the current shadow.
            if ((shadowHistory[word + 3u] != passGroup.historyFrames) ||
                !sdfHistoryReceiverMatches(visibility.identity, shadowHistory[word + 1u], previousT, asfloat(shadowHistory[word + 2u]))) {
                return false;
            }
            taps[5u + offset.x + (4u * offset.y)] = sdfUnpackShadowVisibility(shadowHistory[word]);
        }
    }
    history = puckReconstructionCombine(footprint, taps);
    return true;
}

void sdfShadowStage(SdfPixel p) {
    uint record = 0u;
    SdfVisibility visibility = (SdfVisibility)0;
    SdfVisibilityNormal surfaceNormal = (SdfVisibilityNormal)0;
    float traveled = max(p.marchStart, 0.0);
    bool shades = false;

    if (p.active) {
        record = worldVisibilityRecord(p.pixel, p.viewIndex);
        visibility = sdfLoadVisibility(record);
        traveled = visibility.t;
        shades = (sdfVisibilityHit(visibility) && (sdfVisibilityKind(visibility.identity) != SDF_VISIBILITY_KIND_MESH)
            && !sdfScreenSurfaceShades(visibility.material));
        if (shades) {
            surfaceNormal = sdfLoadVisibilityNormal(record);
        }
    }

    bool finalMode = worldFinalShadingMode(p.viewMode);
    float shadowReach = (ShadowMaxDistance * worldShadowDistanceScale());
    bool cullOn = worldShadowCullEnabled();
    bool shadowsOn = !worldSoftShadowsDisabled();
    float3 surfacePoint = (p.rayOrigin + (p.rayDirection * traveled));
    float shadingStepScale = (sdfStepScale() * max(surfaceNormal.gradientMagnitude, GradientMagnitudeFloor));
    float4 stableVisibility = float4(1.0, 1.0, 1.0, 1.0);
    float2 incoming = float2(1.0, 1.0);
    bool temporalShadows = ((passGroup.temporal != 0u) && (passGroup.shadowSlotCount > 1u) && finalMode);
    bool amortize = (temporalShadows && (passGroup.shadowAmortize != 0u));
    float4 history = float4(1.0, 1.0, 1.0, 1.0);
    // Receiver validation belongs to K reuse. The color resolve validates its own receiver when K is marched fresh.
    bool receiverValid = !amortize;
    if (amortize && shades) { receiverValid = sdfShadowHistoryAt(record, visibility, surfacePoint, p.viewIndex, history); }
    uint shadowReactive = 0u;
    sdfEvalCount = 0.0;

    [loop]
    for (uint shadowSlot = 0u; (shadowSlot < worldShadowMarchCount()); shadowSlot++) {
        int lightIndex = worldShadowMarchLight(shadowSlot);
        bool hasLight = ((lightIndex >= 0) && ((uint)lightIndex < passGroup.lightCount));
        float3 direction = (hasLight ? sdfLights[(uint)lightIndex].Direction : SdfSunDirection);
        bool lit = (shades && finalMode && shadowsOn && hasLight && (dot(surfaceNormal.normal, direction) > 0.0));
        uint groupGather = (cullOn ? 1u : 0u);
        bool secondary = (amortize && (shadowSlot > 0u) && (shadowSlot < passGroup.shadowSlotCount));
        bool reactiveSecondary = (temporalShadows && (shadowSlot > 0u) && (shadowSlot < passGroup.shadowSlotCount));
        sdfShadowMotionActive = reactiveSecondary;
        if (reactiveSecondary) {
            if (p.lane == 0u) { sdfShadowGatherMoved = 0u; }
            GroupMemoryBarrierWithGroupSync();
        }

        // Only uniform frame/light facts guard the gather; unlit and out-of-extent lanes still participate.
        if (finalMode && cullOn && !worldUseCameraTileShadowMask() && shadowsOn && hasLight) {
            groupGather = sdfShadowGatherGroup(lit, surfacePoint, direction, shadowReach, lightIndex, p.lane);
        }
        else if (reactiveSecondary && hasLight) {
            sdfShadowMovedFlat(p.lane);
            GroupMemoryBarrierWithGroupSync();
        }

        uint decision = SDF_SHADOW_DECISION_INTERLEAVED;
        if (reactiveSecondary) {
            uint bit = (1u << shadowSlot);
            if ((passGroup.shadowOwnershipReject & bit) != 0u) { decision = SDF_SHADOW_DECISION_OWNERSHIP; }
            else if ((passGroup.shadowLightReject & bit) != 0u) { decision = SDF_SHADOW_DECISION_LIGHT_MOTION; }
            else if (sdfShadowGatherMoved != 0u) { decision = SDF_SHADOW_DECISION_OCCLUDER_MOTION; }
            else if (!receiverValid) { decision = SDF_SHADOW_DECISION_RECEIVER; }
            else if (amortize && (((p.pixel.x & 1u) | ((p.pixel.y & 1u) << 1u)) != (passGroup.historyFrames & 3u))) {
                decision = SDF_SHADOW_DECISION_REPROJECTED;
            }
        }
        if (reactiveSecondary && shades && (decision >= SDF_SHADOW_DECISION_OWNERSHIP) && (decision <= SDF_SHADOW_DECISION_RECEIVER)) {
            shadowReactive = 1u;
        }

        float marched = 1.0;
        uint before = sdfWorkSteps;
        if (lit && (decision != SDF_SHADOW_DECISION_REPROJECTED)) {
            uint fallbackMask = ((cullOn && (groupGather == 1u)) ? p.instanceMaskBase : SDF_INSTANCE_MASK_ALL);
            sdfSecondaryMarchActive = true;
            sdfShadowMaskActive = (groupGather == 2u);
            sdfShadowParticipationActive = true;
            marched = softShadowVisibility(surfacePoint, surfaceNormal.normal, direction, fallbackMask, shadingStepScale, shadowReach, lightIndex);
            sdfShadowParticipationActive = false;
            sdfShadowMaskActive = false;
            sdfSecondaryMarchActive = false;
        }
        if (lit && (decision == SDF_SHADOW_DECISION_REPROJECTED)) { marched = history[shadowSlot]; }
        if (lit && secondary) {
            puckCountShadowDecision(decision, shadowSlot, (sdfWorkSteps - before));
            // Named details are added to the pass by GpuWorkLedger; its plain row excludes their work.
            sdfWorkSteps = before;
        } else {
            puckCountShadow(shadowSlot, (sdfWorkSteps - before));
        }

        if (shadowSlot < passGroup.shadowSlotCount) {
            stableVisibility[shadowSlot] = marched;
        } else {
            incoming[shadowSlot - passGroup.shadowSlotCount] = marched;
        }
        GroupMemoryBarrierWithGroupSync();
    }

    if (!p.active) {
        return;
    }
    sdfStoreVisibilityQueries(record, SDF_VISIBILITY_SHADOW_QUERIES_WORD, sdfEvalCount);
    sdfStoreVisibilityShadows(record, stableVisibility);
    if (temporalShadows) {
        uint word = (SDF_SHADOW_HISTORY_WORDS * (((p.viewIndex * passGroup.imageExtent.y + p.pixel.y) * passGroup.imageExtent.x) + p.pixel.x));
        if (amortize) {
            shadowHistoryRW[word] = sdfPackShadowVisibility(stableVisibility);
            shadowHistoryRW[word + 1u] = visibility.identity;
            shadowHistoryRW[word + 2u] = asuint(visibility.t);
            shadowHistoryRW[word + 3u] = (passGroup.historyFrames + 1u);
            sdfWorkTexels += SDF_SHADOW_HISTORY_WORDS;
        } else {
            sdfWorkTexels += 1u;
        }
        // Freshly marched K must not be mixed with old-shadow color either. The off-switch writes only this
        // current-frame metadata word; it neither reads nor writes the four K-history words above.
        shadowHistoryRW[word + 4u] = shadowReactive;
    }
#if SDF_SHADOW_FADE_SLOTS > 0
    if (passGroup.shadowFadeCount > 0u) {
#if SDF_SHADOW_FADE_SLOTS == 1
        incomingVisibilityRW[p.pixel] = incoming.x;
#else
        incomingVisibilityRW[p.pixel] = incoming;
#endif
        sdfWorkTexels += 1u;
    }
#endif
}

#endif
#endif
