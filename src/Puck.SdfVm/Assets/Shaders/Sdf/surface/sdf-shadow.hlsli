// One rolled gather/march site serves each stable slot and each active incoming handoff. All lanes reach each gather
// and the barrier behind its march before any lane clears the shared mask for the next light.
#ifndef SURFACE_SDF_SHADOW_HLSLI
#define SURFACE_SDF_SHADOW_HLSLI
#ifdef SDF_SHADOW_PASS

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
    sdfEvalCount = 0.0;

    [loop]
    for (uint shadowSlot = 0u; (shadowSlot < worldShadowMarchCount()); shadowSlot++) {
        int lightIndex = worldShadowMarchLight(shadowSlot);
        bool hasLight = ((lightIndex >= 0) && ((uint)lightIndex < passGroup.lightCount));
        float3 direction = (hasLight ? sdfLights[(uint)lightIndex].Direction : SdfSunDirection);
        bool lit = (shades && finalMode && shadowsOn && hasLight && (dot(surfaceNormal.normal, direction) > 0.0));
        uint groupGather = (cullOn ? 1u : 0u);

        // Only uniform frame/light facts guard the gather; unlit and out-of-extent lanes still participate.
        if (finalMode && cullOn && !worldUseCameraTileShadowMask() && shadowsOn && hasLight) {
            groupGather = sdfShadowGatherGroup(lit, surfacePoint, direction, shadowReach, lightIndex, p.lane);
        }

        float marched = 1.0;
        uint before = sdfWorkSteps;
        if (lit) {
            uint fallbackMask = ((cullOn && (groupGather == 1u)) ? p.instanceMaskBase : SDF_INSTANCE_MASK_ALL);
            sdfSecondaryMarchActive = true;
            sdfShadowMaskActive = (groupGather == 2u);
            sdfShadowParticipationActive = true;
            marched = softShadowVisibility(surfacePoint, surfaceNormal.normal, direction, fallbackMask, shadingStepScale, shadowReach, lightIndex);
            sdfShadowParticipationActive = false;
            sdfShadowMaskActive = false;
            sdfSecondaryMarchActive = false;
        }
        puckCountShadow(shadowSlot, (sdfWorkSteps - before));

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
    if (sdfEvalCount > 0.0) {
        SdfVisibilitySurface surface = sdfLoadVisibilitySurface(record);
        surface.queries += sdfEvalCount;
        sdfStoreVisibilitySurface(record, surface);
    }
    sdfStoreVisibilityShadows(record, stableVisibility);
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
