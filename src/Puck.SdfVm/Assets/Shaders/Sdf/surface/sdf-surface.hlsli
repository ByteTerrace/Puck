#ifndef PUCK_SDF_SURFACE_HLSLI
#define PUCK_SDF_SURFACE_HLSLI

// The surface and ambient stages: each reads the pixel's visibility record (sdf-visibility.hlsli), which primary wrote,
// and writes its own rows. Every active pixel receives neutral N and S rows before the next dispatch, including misses
// and diagnostic views. This avoids stale AO when a pose, camera, material, viewport rectangle or lighting setting
// changes. Each writer compiles only into its own pass: views binds the record read-only.
#ifdef SDF_SURFACE_PASS
void sdfResolveSurface(float3 surfacePoint, float3 ray, bool hit, int material, int mode, uint mask,
    float primaryRadius, float footprint, uint record) {
    float3 normal = 0.0;
    float gradientMagnitude = 1.0, curvature = 0.0;
    float initialQueries = sdfEvalCount;
    bool finalMode = worldFinalShadingMode(mode);
    bool sampledScreen = false;
#ifdef SDF_SCREEN_SOURCES
    if (hit && finalMode) {
        sampledScreen = sdfScreenSurfaceShades(material);
    }
#endif
    bool needsNormal = hit && (mode == DebugViewModeNormals || (finalMode && !sampledScreen));
    if (needsNormal) {
        sdfDetailShadingActive = true;
        // The curvature and tap paths share one probe call, so the kernel inlines the interpreter once for both.
        bool curvatureShading = worldCurvatureShadingEnabled();
        if (curvatureShading || worldUseTapNormals())
            normal = calculateTapNormal(surfacePoint, mask, curvatureShading, primaryRadius, curvature, gradientMagnitude);
        else normal = calculateNormalAnalytic(surfacePoint, mask, gradientMagnitude);
        sdfDetailShadingActive = false;
    }
    SdfVisibilityNormal geometric;
    geometric.normal = normal;
    geometric.gradientMagnitude = gradientMagnitude;
    SdfVisibilitySurface surface;
    surface.curvature = curvature;
    surface.queries = sdfEvalCount - initialQueries;
    surface.ambient = 1.0;
    surface.flags = needsNormal && finalMode && material < SDF_SCREEN_MATERIAL ? 1u : 0u;
    sdfStoreVisibilityNormal(record, geometric);
    sdfStoreVisibilitySurface(record, surface);
}

// A mesh pixel's N and S rows: its triangle's surface normal (sdfMeshSurfaceNormal, or a textured mesh's sampled normal),
// no curvature, and its ambient occlusion: a textured mesh's baked occlusion, one otherwise. Its lit flag stays clear, so
// the ambient pass leaves the occlusion as written; mesh pixels shade with neutral shadows, and views skips their shadow
// march.
void sdfResolveMeshSurface(float3 normal, float ambient, uint record) {
    SdfVisibilityNormal geometric;
    geometric.normal = normal;
    geometric.gradientMagnitude = 1.0;
    SdfVisibilitySurface surface;
    surface.curvature = 0.0;
    surface.queries = 0.0;
    surface.ambient = ambient;
    surface.flags = 0u;
    sdfStoreVisibilityNormal(record, geometric);
    sdfStoreVisibilitySurface(record, surface);
}

// The surface stage: resolves an active pixel's normal and curvature from the field, or a mesh hit's from its triangle,
// which the record names, a textured mesh's normal and occlusion from the atlases, and an impostor card's normal from the
// impostor's views, facing the camera as the geometric normal does.
void sdfSurfaceStage(SdfPixel p) {
    if (!p.active) {
        return;
    }

    uint record = worldVisibilityRecord(p.pixel, p.viewIndex);
    SdfVisibility visibility = sdfLoadVisibility(record);

    sdfEvalCount = (float)sdfVisibilityQueries(visibility);

    if (sdfVisibilityKind(visibility.identity) == SDF_VISIBILITY_KIND_MESH) {
        uint draw = sdfVisibilitySource(visibility.identity);
        uint triangleIndex = sdfVisibilityMeshTriangle(record);
        float3 meshPoint = (p.rayOrigin + (p.rayDirection * visibility.t));
        float meshAmbient = 1.0;
        float3 meshNormal;

        if (sdfMeshIsImpostor(draw)) {
            meshNormal = sdfImpostorSurfaceAt(draw, p.rayOrigin, p.rayDirection, visibility.t, p.pixelFootprint).normal;
        } else {
            meshNormal = sdfMeshSurfaceNormal(draw, triangleIndex, meshPoint, p.rayDirection);
        }

        if (sdfMeshTextured(draw)) {
            SdfMeshTexel meshTexel = sdfMeshTexelAt(draw, triangleIndex, meshPoint, (p.pixelFootprint * visibility.t));

            meshNormal = sdfMeshFaceCamera(sdfMeshTexelNormal(draw, meshTexel), sdfMeshFace(sdfMeshRecord(draw), triangleIndex), p.rayDirection);
            meshAmbient = sdfMeshTexelOcclusion(meshTexel);
        }

        sdfResolveMeshSurface(meshNormal, meshAmbient, record);

        return;
    }

    SdfVisibilityCoverage coverage = sdfLoadVisibilityCoverage(record);

    sdfResolveSurface((p.rayOrigin + (p.rayDirection * visibility.t)), p.rayDirection, sdfVisibilityHit(visibility), visibility.material,
        p.viewMode, p.instanceMaskBase, coverage.terminalRadius, (p.pixelFootprint * visibility.t), record);
}
#endif

#ifdef SDF_AMBIENT_PASS
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
    SdfInstanceGridHeader grid = sdfLoadInstanceGridHeader(offset, count);
    sdfSummarizeGroupMask(true, (grid.enabled ? grid.footprintPad : 1.0e30), lane);
}
#endif

void sdfResolveAmbient(float3 surfacePoint, uint cameraMask, uint2 pixel, uint viewport, uint lane, bool active) {
    if (worldAoDisabled()) return; // Uniform view setting; the surface pass already wrote neutral AO.
    uint record = worldVisibilityRecord(pixel, viewport);
    SdfVisibilitySurface info = (SdfVisibilitySurface)0;
    if (active) info = sdfLoadVisibilitySurface(record);
    bool lit = active && (info.flags & 1u) != 0u;
    bool fast = worldUseFastAmbientOcclusion();
#ifdef SDF_GROUP_SHADOW_GATHER
    if (!fast) sdfBuildAmbientMask(surfacePoint, lit, lane);
#endif
    if (!lit) {
        if (active) sdfStoreVisibilityQueries(record, SDF_VISIBILITY_AMBIENT_QUERIES_WORD, 0.0);
        return;
    }
    SdfVisibilityNormal surface = sdfLoadVisibilityNormal(record);
    float stepScale = sdfProgramLayout.stepScale * max(surface.gradientMagnitude, GradientMagnitudeFloor);
    uint mask = fast ? cameraMask : SDF_INSTANCE_MASK_ALL;
    float initialQueries = sdfEvalCount;
#ifdef SDF_GROUP_SHADOW_GATHER
    sdfAmbientMaskActive = !fast;
#endif
    // Occlusion follows the geometric normal. Material Soften changes the later lighting normal only.
    sdfSecondaryMarchActive = true;
    info.ambient = calcAO(surfacePoint, surface.normal, mask, stepScale, fast);
    sdfSecondaryMarchActive = false;
#ifdef SDF_GROUP_SHADOW_GATHER
    sdfAmbientMaskActive = false;
#endif
    sdfStoreVisibilityQueries(record, SDF_VISIBILITY_AMBIENT_QUERIES_WORD, sdfEvalCount - initialQueries);
    sdfStoreVisibilitySurface(record, info);
}

// The ambient stage: every lane, active or not, reaches the group's ambient gather, so a lane past the render extent
// resolves at its march start and stores nothing.
void sdfAmbientStage(SdfPixel p) {
    float traveled = max(p.marchStart, 0.0);

    sdfEvalCount = 0.0;

    if (p.active) {
        traveled = sdfLoadVisibility(worldVisibilityRecord(p.pixel, p.viewIndex)).t;
    }

    sdfResolveAmbient((p.rayOrigin + (p.rayDirection * traveled)), p.instanceMaskBase, p.pixel, p.viewIndex, p.lane, p.active);
}
#endif

#endif
