#ifndef PUCK_SDF_SURFACE_HLSLI
#define PUCK_SDF_SURFACE_HLSLI

#ifdef SDF_PRIMARY_READ
// Surface and ambient share the primary grid and its hit record. Every active pixel receives neutral
// secondary rows before the next dispatch, including misses and diagnostic views. This avoids stale AO
// when a pose, camera, material, viewport rectangle or lighting setting changes.
void sdfResolveSurface(float3 surfacePoint, float3 ray, bool hit, int material, int mode, uint mask,
    float primaryRadius, float footprint, uint offset) {
    float3 normal = 0.0;
    float gradientMagnitude = 1.0, curvature = 0.0;
    float initialQueries = sdfEvalCount;
    bool finalMode = mode <= 0 || mode >= DebugViewModeCount || mode == DebugViewModeEvals;
    bool sampledScreen = false;
#ifdef SDF_SCREEN_SOURCES
    if (hit && finalMode) {
        float3 unusedColor;
        sampledScreen = sampleScreenSurface(material, surfacePoint, ray, footprint, unusedColor);
    }
#endif
    bool needsNormal = hit && (mode == DebugViewModeNormals || (finalMode && !sampledScreen));
    if (needsNormal) {
        sdfDetailShadingActive = true;
        if (worldCurvatureShadingEnabled())
            normal = calculateNormalCurvature(surfacePoint, mask, primaryRadius, curvature, gradientMagnitude);
        else if (worldUseTapNormals())
            normal = calculateNormal(surfacePoint, mask, gradientMagnitude);
        else normal = calculateNormalAnalytic(surfacePoint, mask, gradientMagnitude);
        sdfDetailShadingActive = false;
    }
    uint flags = needsNormal && finalMode && material < SDF_SCREEN_MATERIAL ? 1u : 0u;
    sdfStorePrimaryRow(offset + 12u, float4(normal, gradientMagnitude));
    sdfStorePrimaryRow(offset + 16u, float4(curvature, sdfEvalCount - initialQueries, 1.0, asfloat(flags)));
}

#ifdef SDF_GROUP_SHADOW_GATHER
// AO rays are short normal-displaced probes. A box enclosing the group's hits, expanded by their maximum
// displacement, covers all three rungs regardless of each normal. Complete expressions whose positive
// sublevel boxes miss that region cannot contribute; unsupported expressions remain in the mask.
void sdfBuildAmbientMask(float3 surfacePoint, bool lit, uint lane) {
    sdfShadowGatherPoints[lane] = float4(surfacePoint, lit ? 1.0 : 0.0);
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0u) {
        float3 low = 1e20, high = -1e20;
        [loop] for (uint i = 0u; i < SDF_GROUP_SHADOW_LANES; i++) {
            float4 entry = sdfShadowGatherPoints[i];
            if (entry.w > 0.5) { low = min(low, entry.xyz); high = max(high, entry.xyz); }
        }
        sdfAmbientGatherLow = low - AmbientOcclusionReach;
        sdfAmbientGatherHigh = high + AmbientOcclusionReach;
    }
    GroupMemoryBarrierWithGroupSync();
    uint count = min(sdfInstanceCount(), SDF_MAX_INSTANCES), offset = sdfInstanceDirectoryOffset();
    bool contactCull = sdfCanTracePartsIndependently();
    for (uint word = lane; word < SDF_SHADOW_MASK_WORDS; word += SDF_GROUP_SHADOW_LANES) {
        uint bits = 0u, end = min((word + 1u) * 32u, count);
        for (uint index = word * 32u; index < end; index++) {
            if (sdfInstanceBoundAt(offset, index).w >= 0.0 &&
                (!contactCull || !sdfInstanceOutsideContactBox(index, sdfAmbientGatherLow, sdfAmbientGatherHigh)))
                bits |= 1u << (index & 31u);
        }
        sdfAmbientMaskWords[word] = bits;
    }
    GroupMemoryBarrierWithGroupSync();
}
#endif

void sdfResolveAmbient(float3 surfacePoint, uint cameraMask, uint2 pixel, uint viewport, uint lane, bool active) {
    if (worldAoDisabled()) return; // Uniform view setting; the surface pass already wrote neutral AO.
    uint offset = sdfPrimaryHitOffset(pixel, viewport);
    float4 info = 0.0;
    if (active) info = sdfLoadPrimaryRow(offset + 16u);
    bool lit = active && (asuint(info.w) & 1u) != 0u;
    bool fast = worldUseFastAmbientOcclusion();
#ifdef SDF_GROUP_SHADOW_GATHER
    if (!fast) sdfBuildAmbientMask(surfacePoint, lit, lane);
#endif
    if (!lit) return; // All lanes have passed the group barriers.
    float4 surface = sdfLoadPrimaryRow(offset + 12u);
    float stepScale = sdfProgramLayout.stepScale * max(surface.w, GradientMagnitudeFloor);
    uint mask = fast ? cameraMask : SDF_INSTANCE_MASK_ALL;
    float initialQueries = sdfEvalCount;
#ifdef SDF_GROUP_SHADOW_GATHER
    sdfAmbientMaskActive = !fast;
#endif
    // Occlusion follows the geometric normal. Material Soften changes the later lighting normal only.
    sdfSecondaryMarchActive = true;
    info.z = fast ? calcFastAO(surfacePoint, surface.xyz, mask, stepScale) : calcAO(surfacePoint, surface.xyz, mask, stepScale);
    sdfSecondaryMarchActive = false;
#ifdef SDF_GROUP_SHADOW_GATHER
    sdfAmbientMaskActive = false;
#endif
    info.y += sdfEvalCount - initialQueries;
    sdfStorePrimaryRow(offset + 16u, info);
}
#endif

#endif
