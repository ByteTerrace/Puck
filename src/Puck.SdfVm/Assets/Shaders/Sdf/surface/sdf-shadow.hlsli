// The shadow stage: the key light's soft shadow over the visibility record, between the ambient and views stages. It
// runs only on a frame whose soft shadows are on and that has a shadow light; any other frame skips the pass
// (SdfWorldPassRecorder.Skips), and views then reads nothing of the record's key row.
#ifndef SURFACE_SDF_SHADOW_HLSLI
#define SURFACE_SDF_SHADOW_HLSLI
#ifdef SDF_SHADOW_PASS

// Marches the key light's soft shadow for an active pixel's lit surface and stores its visibility in the record's key
// row, 1 wherever it marches nothing, adding the march's queries to the record's surface queries. Every lane first
// reaches the group shadow gather (sdfShadowGatherGroup, uniform control flow), whose inputs are uniform: the view's
// mode, the engine levers, the reach. A lane that is not lit still publishes, as unlit, and walks its share of the grid.
void sdfShadowStage(SdfPixel p) {
    uint record = 0u;
    SdfVisibility visibility = (SdfVisibility)0;
    float traveled = max(p.marchStart, 0.0);
    bool hit = false;
    bool mesh = false;

    if (p.active) {
        record = worldVisibilityRecord(p.pixel, p.viewIndex);
        visibility = sdfLoadVisibility(record);
        traveled = visibility.t;
        hit = sdfVisibilityHit(visibility);
        mesh = (sdfVisibilityKind(visibility.identity) == SDF_VISIBILITY_KIND_MESH);
    }

    bool finalMode = worldFinalShadingMode(p.viewMode);
    // One scaled reach for both the gather's cull cone and the march's ceiling (world.shadows's reach), so the gathered
    // occluder set is sound for the shadow ray.
    float shadowReach = (ShadowMaxDistance * worldShadowDistanceScale());
    bool cullOn = worldShadowCullEnabled();
    // The group's decision: 2 marches the group's candidate mask (bit-identical to the flat all-instances march,
    // restricted to the instances the group's shadow rays can reach); 1 the camera-tile mask; 0 the flat field, which is
    // cheap for a few-instance program and keeps the grid toggle render-invariant. The cull off marches the flat field,
    // the ground-truth reference.
    uint groupGather = (cullOn ? 1u : 0u);

    if (finalMode && cullOn && !worldUseCameraTileShadowMask() && !worldSoftShadowsDisabled()) {
        groupGather = sdfShadowGatherGroup((hit && !mesh), (p.rayOrigin + (p.rayDirection * traveled)), worldSunDirection(), shadowReach, p.lane);
    }

    if (!p.active) {
        return;
    }

    float keyVisibility = 1.0;

    sdfEvalCount = 0.0;

    // The pixels views lights under the key light: a final-shading hit that is no mesh's and no screen's face.
    if (hit && finalMode && !mesh && !sdfScreenSurfaceShades(visibility.material)) {
        SdfVisibilityNormal surfaceNormal = sdfLoadVisibilityNormal(record);
        float3 keyDirection = worldSunDirection();
        float sunDiffuse = max(dot(surfaceNormal.normal, keyDirection), 0.0);

        if ((sunDiffuse > 0.0) && (passGroup.shadowLight >= 0) && !worldSoftShadowsDisabled()) {
            float3 surfacePoint = (p.rayOrigin + (p.rayDirection * traveled));
            // The program's march clamp composed with the hit's local gradient magnitude (GradientMagnitudeFloor): the
            // one de-scale factor the shadow estimate divides by.
            float shadingStepScale = (sdfStepScale() * max(surfaceNormal.gradientMagnitude, GradientMagnitudeFloor));
            uint shadowFallbackMask = ((cullOn && (groupGather == 1u)) ? p.instanceMaskBase : SDF_INSTANCE_MASK_ALL);

            sdfSecondaryMarchActive = true;
            sdfShadowMaskActive = (groupGather == 2u);
            // Per-instance soft-shadow participation is live for this march only, in every fallback mode, so a
            // shadow-suppressed dynamic instance drops out of each identically.
            sdfShadowParticipationActive = true;
            keyVisibility = softShadowVisibility(surfacePoint, surfaceNormal.normal, keyDirection, shadowFallbackMask, shadingStepScale, shadowReach);
            sdfShadowParticipationActive = false;
            sdfShadowMaskActive = false;
            sdfSecondaryMarchActive = false;

            SdfVisibilitySurface surface = sdfLoadVisibilitySurface(record);

            surface.queries += sdfEvalCount;
            sdfStoreVisibilitySurface(record, surface);
        }
    }

    sdfStoreVisibilityKey(record, keyVisibility);
}

#endif
#endif
