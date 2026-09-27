// renderView, the hit passes' per-pixel body, with the part bounds, the primary march and the surface resolve it calls.
#ifndef PASSES_SDF_RENDER_VIEW_HLSLI
#define PASSES_SDF_RENDER_VIEW_HLSLI
#ifdef SDF_PART_RAY_BOUNDS
#include "../march/sdf-part-bounds.hlsli"
#endif
#include "../march/sdf-primary.hlsli"
#include "../surface/sdf-surface.hlsli"

// The hit passes' per-pixel body: primary records the pixel's visibility, surface and ambient resolve its N and S rows, and
// views shades it from the completed record. Only the hit-pass kernels compile it.
#if defined(SDF_PRIMARY_PASS) || defined(SDF_PRIMARY_READ)
// `lane` is the caller's index within its 8x8 workgroup and `active` whether this lane owns a rendered pixel: an
// inactive lane (past the render extent) still runs the march-free prologue and the group shadow gather's barriers
// (UNIFORM control flow — see sdfShadowGatherGroup) and then returns black, which the caller never stores.
float3 renderView(ViewportData view, float2 localUv, float marchStart, float firstExit, float secondEntry, float farBound, uint instanceMaskBase, float pixelFootprint, uint2 pixel, uint viewIndex, uint lane, bool active) {
    float3 rayOrigin = view.position.xyz;
    float3 rayDirection = cameraRayDirection(view, localUv);
    int viewMode = (int)round(view.forward.w);
    float farDistance = worldFarDistance(view);

    // Cone entry is a conservative ray distance; rasterization clips at forward distance ConeNear.
    if (marchStart >= 0.0) {
        marchStart = max(marchStart, (ConeNear / dot(rayDirection, view.forward.xyz)));
    }

    sdfEvalCount = 0.0; // fresh tally for this pixel — see world.debug-view evals (case 10 below)

    float traveled = max(marchStart, 0.0);
    bool hitSurface = false;
    int material = 0;
    // The winning instance's four anonymous lane values and frame.
    float4 hitLanes = float4(0.0, 0.0, 0.0, 0.0);
    int hitFrameSlot = -1;
    // Material blend at smooth seams (field/sdf-point.hlsli's sdfMaterialBlendWeight): captured from the ACCEPT-sample march call
    // alongside `material`, because the normal/AO/shadow map calls after the loop clobber the per-thread channel. Weight 0
    // (no smooth seam within a blend radius of the hit) => the shade below is the exact table lookup, unchanged.
    float materialBlendWeight = 0.0;
    int materialBlendOther = 0;
    int marchStep = 0;
    // Silhouette coverage AA: the CLAMPED field at the accepted hit (the terminal-step residual), captured by both march
    // paths at their hit-accept. The coverage metric derived from it in the epilogue must live in the SAME units as
    // the footprint-adaptive termination test (clamped radius vs hitThreshold) — do NOT divide by stepScale here.
    // The divide-back that is correct for softShadowVisibility/calcAO (world-space geometric comparisons) is WRONG for this
    // metric: de-scaling inflates the ratio by 1/stepScale and saturates solid hits, erasing the coverage signal.
    float terminalRadius = 0.0;
    // The footprint-adaptive hit threshold captured at the SAME accept step as terminalRadius (both march paths). The
    // epilogue's coverage = terminalRadius / hitThreshold, and `traveled` is frozen at the hit after the loop breaks, so
    // this equals a recompute of max(SurfaceEpsilon, pixelFootprint * traveled) there — capture once instead.
    float terminalHitThreshold = SurfaceEpsilon;
    // The per-program Lipschitz clamp, shared by softShadowVisibility and calcAO. Both divide it back out of
    // world-space comparisons; the coverage ratio stays in the clamped units of the termination test.
    float stepScale = sdfStepScale();

    // The SLICE view never marches: it evaluates the field on a plane instead (its case below), and the beam prepass
    // force-survives every tile for it — marching those would be pure waste (sky pixels would run the full MaxSteps).
    // MASK (reads the tile mask buffer directly) and OVERSHOOT (runs its OWN two marches in its case) skip the primary
    // march too — for MASK it is unused work, for OVERSHOOT running it AS WELL would be a third march. Every non-debug
    // and every OTHER debug mode still marches exactly as before (the added compares are false for them).
    // A mesh surface covering the pixel. The passes after primary read it as the record's kind; primary reads the mesh
    // pass's target, bounds its march by the mesh's ray parameter and keeps the SDF hit only when it is strictly nearer.
    bool meshPixel = false;
#ifdef SDF_PRIMARY_READ
    if (active) {
        uint record = worldVisibilityRecord(pixel, viewIndex);
        SdfVisibility visibility = sdfLoadVisibility(record);
        SdfVisibilityCoverage coverage = sdfLoadVisibilityCoverage(record);
        traveled = visibility.t;
        terminalRadius = coverage.terminalRadius;
        terminalHitThreshold = coverage.threshold;
        material = visibility.material;
        hitLanes = sdfLoadVisibilityLanes(record);
        hitFrameSlot = sdfVisibilityFrameSlot(visibility);
        materialBlendWeight = coverage.blendWeight;
        materialBlendOther = coverage.blendOther;
        marchStep = (int)sdfVisibilitySteps(visibility);
        sdfEvalCount = (float)sdfVisibilityQueries(visibility);
        hitSurface = sdfVisibilityHit(visibility);
        meshPixel = (sdfVisibilityKind(visibility.identity) == SdfVisibilityKindMesh);
    }
#else
    SdfMeshSample meshHit = (SdfMeshSample)0;

    if (active) {
        meshHit = sdfMeshSampleAt(pixel);
    }

    // The march ends at the nearest of the far distance, the tile's far bound and the mesh, and does not start at or
    // past it: nothing it could accept there would win.
    float marchBound = min(farDistance, (meshHit.covered ? min(farBound, meshHit.t) : farBound));

    if ((marchStart >= 0.0) && (marchStart < marchBound) && (viewMode != DebugViewModeSlice) && (viewMode != DebugViewModeMask) && (viewMode != DebugViewModeOvershoot)) {
        SdfPrimaryMarch primary = sdfTracePrimary(rayOrigin, rayDirection, marchStart, firstExit, secondEntry,
            marchBound, farDistance, instanceMaskBase, pixelFootprint);
        traveled = primary.traveled;
        terminalRadius = primary.radius;
        terminalHitThreshold = primary.threshold;
        material = primary.material;
        hitLanes = primary.lanes;
        hitFrameSlot = primary.frameSlot;
        materialBlendWeight = primary.blendWeight;
        materialBlendOther = primary.blendOther;
        marchStep = (int)primary.steps;
        hitSurface = primary.found;
    }

    // At equal depth the mesh wins: the SDF surface is kept only when it is strictly nearer. A mesh pixel carries no SDF
    // frame, lanes or seam blend, and its coverage threshold is one so the silhouette blend reads it as solid.
    if (meshHit.covered && !(hitSurface && (traveled < meshHit.t))) {
        meshPixel = true;
        hitSurface = true;
        traveled = meshHit.t;
        material = sdfMeshMaterial(meshHit.draw, meshHit.triangleIndex);
        hitLanes = float4(0.0, 0.0, 0.0, 0.0);
        hitFrameSlot = -1;
        materialBlendWeight = 0.0;
        materialBlendOther = 0;
        terminalRadius = 0.0;
        terminalHitThreshold = 1.0;
    }
#endif // SDF_PRIMARY_READ

#if defined(SDF_SURFACE_PASS)
    if (active && meshPixel) {
        SdfMeshSample meshSurface = sdfMeshSampleAt(pixel);

        sdfResolveMeshSurface(sdfMeshSurfaceNormal(meshSurface.draw, meshSurface.triangleIndex, (rayOrigin + (rayDirection * meshSurface.t)), rayDirection), worldVisibilityRecord(pixel, viewIndex));
    } else if (active) {
        sdfResolveSurface(rayOrigin + rayDirection * traveled, rayDirection, hitSurface, material,
            viewMode, instanceMaskBase, terminalRadius, pixelFootprint * traveled, worldVisibilityRecord(pixel, viewIndex));
    }
    return 0.0;
#elif defined(SDF_AMBIENT_PASS)
    sdfResolveAmbient(rayOrigin + rayDirection * traveled, instanceMaskBase, pixel, viewIndex, lane, active);
    return 0.0;
#elif defined(SDF_PRIMARY_PASS)
    if (active) {
        uint record = worldVisibilityRecord(pixel, viewIndex);
        SdfVisibility visibility;
        visibility.t = traveled;
        visibility.identity = (meshPixel
            ? sdfVisibilityIdentity(SdfVisibilityKindMesh, meshHit.draw)
            : sdfVisibilitySdfIdentity(hitSurface, hitFrameSlot));
        visibility.material = material;
        visibility.flags = sdfVisibilityFlags(marchStep, sdfEvalCount);
        SdfVisibilityCoverage coverage;
        coverage.terminalRadius = terminalRadius;
        coverage.threshold = terminalHitThreshold;
        coverage.blendWeight = materialBlendWeight;
        coverage.blendOther = materialBlendOther;
        sdfStoreVisibility(record, visibility);
        sdfStoreVisibilityCoverage(record, coverage);
        sdfStoreVisibilityLanes(record, hitLanes);
    }
    return 0.0;
#else
    // THE GROUP SHADOW GATHER — at the one seam every lane of the workgroup reaches (the march loops above carry no
    // barrier; the epilogue below is per-lane divergent): reduce the group's hit points and build ONE shadow candidate
    // mask for all of them (sdfShadowGatherGroup, uniform control flow). The decisions feeding it are uniform: the
    // view's mode, the engine levers, the reach. A lane that is not lit still publishes (as unlit) and still walks its
    // share of the grid — its own epilogue never marches a shadow, so nothing it gathered is wasted on itself.
#ifdef SDF_SCREEN_SOURCES
    bool cullOn = worldShadowCullEnabled();
    uint groupGather = (cullOn ? 1u : 0u); // without the group gather: the camera-tile mask (1) or the flat field (0)
#ifdef SDF_GROUP_SHADOW_GATHER
    {
        bool finalShadingMode = ((viewMode <= 0) || (viewMode >= DebugViewModeCount) || (viewMode == DebugViewModeEvals));
        bool groupGatherWanted = (finalShadingMode && cullOn && !worldUseCameraTileShadowMask() && !worldSoftShadowsDisabled());

        if (groupGatherWanted) {
            float groupShadowReach = (ShadowMaxDistance * worldShadowDistanceScale());

            groupGather = sdfShadowGatherGroup((hitSurface && !meshPixel), (rayOrigin + (rayDirection * traveled)), worldSunDirection(), groupShadowReach, lane);
        }
    }
#endif
#endif

    if (!active) {
        return float3(0.0, 0.0, 0.0); // past the render extent: the caller stores nothing (no barrier follows)
    }

    float3 normal = float3(0.0, 0.0, 0.0);
    float3 color = skyColor(rayDirection);

    if (hitSurface) {
        float3 surfacePoint = (rayOrigin + (rayDirection * traveled));
        // EVALS rides the SAME epilogue as final shading (normal, soft shadow, AO, screen sampling, coverage-AA):
        // the whole point of the heatmap is to tally what a REAL lit pixel costs, so it cannot take the cheap
        // switch-only shortcut every other debug mode does.
        bool useFinalShading = ((viewMode <= 0) || (viewMode >= DebugViewModeCount) || (viewMode == DebugViewModeEvals));
        bool sampledScreen = false;
#ifdef SDF_SCREEN_SOURCES
        if (useFinalShading) {
            // A bound screen source wins over BOTH the flat sentinel and the unbound glass: emissive/unlit
            // (the diegetic screen is its own light source, like a real display — no scene lighting dims or tints it),
            // but shaped by the CRT glass face (curvature/bezel/scanlines/vignette/glint/bloom in sampleScreenSurface)
            // before the shared distance fog. The screen ALSO lights the room — see the screen-light loop below.
            sampledScreen = sampleScreenSurface(material, surfacePoint, rayDirection, (pixelFootprint * traveled), color);
        }
#endif

        bool needsLitColor = (useFinalShading && !sampledScreen);
        bool needsNormal = ((viewMode == DebugViewModeNormals) || needsLitColor);

        float curvature = 0.0; // level-set mean curvature at the hit (drives the stylized cavity/rim/ink terms below)
        bool curvatureShading = worldCurvatureShadingEnabled();
        // The hit's local (program-stepScale-EXCLUDED) field gradient magnitude — see GradientMagnitudeFloor's
        // remarks. Defaults to 1 (no correction) so a view that skips needsNormal never reaches the shadow/AO
        // branches below, which are gated on needsLitColor and therefore always imply needsNormal ran.
        float gradientMagnitude = 1.0;

        SdfVisibilitySurface surfaceInfo = sdfLoadVisibilitySurface(worldVisibilityRecord(pixel, viewIndex));
        sdfEvalCount += surfaceInfo.queries;
        if (needsNormal) {
            SdfVisibilityNormal surfaceNormal = sdfLoadVisibilityNormal(worldVisibilityRecord(pixel, viewIndex));
            normal = surfaceNormal.normal;
            gradientMagnitude = surfaceNormal.gradientMagnitude;
            curvature = surfaceInfo.curvature;
        }

        if (needsLitColor) {
            // The shadow light's Lambert term under its soft-shadow visibility (the ambient lights still fill shadowed
            // regions, so shadows read soft, not black). The march is skipped where the surface faces away from the
            // light, where no light shadows, or when soft shadows are disabled (world.shadows off; the light then
            // goes unshadowed). The unbound-glass branch below consumes sunDiffuse too, so this march is
            // not dead there.
            float3 keyDirection = worldSunDirection();
            float sunDiffuse = max(dot(normal, keyDirection), 0.0);
            float keyVisibility = 1.0;
            // Local gradient-scaled de-scale (see GradientMagnitudeFloor): composes multiplicatively with the
            // program's own stepScale into ONE effective de-scale factor for the shadow/AO shading ESTIMATES —
            // softShadowVisibility/calcAO/calcFastAO treat it exactly like stepScale (their only uses of the
            // parameter are a world-space ratio and a clamped-units threshold, both wanting the SAME correction
            // whether it comes from the program's global march clamp or the hit's own local gradient magnitude); it
            // never reaches march step-length/soundness logic, which stays keyed on the raw `stepScale` alone.
            float shadingStepScale = (stepScale * max(gradientMagnitude, GradientMagnitudeFloor));

            // The environment scales dim the room so the diegetic screen glow dominates; the overworld sets them low per
            // frame.
            float ambientScale = passGroup.ambientScale;
            float sunScale = passGroup.sunScale;

            if ((sunDiffuse > 0.0) && (worldShadowLightIndex() >= 0) && !worldSoftShadowsDisabled() && !meshPixel) {
                // ONE shared scaled reach for BOTH the gather cull cone and the march ceiling (world.shadows's
                // reach, worldShadowDistanceScale) — they MUST use the same length or the gathered occluder set is
                // unsound for the shadow ray.
                float shadowReach = (ShadowMaxDistance * worldShadowDistanceScale());
                sdfSecondaryMarchActive = true;
#ifdef SDF_SCREEN_SOURCES
                // The shadow GRID CULL (default ON). The group phase above built this workgroup's shadow candidate mask
                // (sdfShadowMaskWords, groupshared) and decided the fallback for every lane: 2 = mask BUILT — march it
                // (the cull, bit-identical to the flat all-instances march, restricted to the instances the group's
                // shadow rays can reach); 1 = the camera-tile lever is set → the camera-tile mask; 0 = NO grid → the
                // flat all-instances fallback, which is cheap for a few-instance program and keeps the grid toggle
                // render-invariant. The cull OFF marches flat all-instances — the ground-truth reference.
                uint gather = groupGather;
                bool culled = (gather == 2u);
                uint shadowFallbackMask = ((cullOn && (gather == 1u)) ? instanceMaskBase : SDF_INSTANCE_MASK_ALL);

                sdfShadowMaskActive = culled;
                // Per-instance soft-shadow participation is live for THIS march ONLY (set unconditionally, not gated on
                // `culled`): all three fallback modes resolve through sdfNextVisibleInstanceRange, so a shadow-suppressed
                // dynamic instance (packed position.w > 0.5) must drop out of every one of them identically.
                sdfShadowParticipationActive = true;
                keyVisibility = softShadowVisibility(surfacePoint, normal, keyDirection, shadowFallbackMask, shadingStepScale, shadowReach);
                sdfShadowParticipationActive = false;
                sdfShadowMaskActive = false;
#else
                keyVisibility = softShadowVisibility(surfacePoint, normal, keyDirection, instanceMaskBase, shadingStepScale, shadowReach);
#endif
                sdfSecondaryMarchActive = false;
                sunDiffuse *= keyVisibility;
            }

            if (material >= SDF_SCREEN_MATERIAL) {
                // The unbound glass: a declared screen with no source bound this frame (or the plain
                // sentinel). Unlit apart from a faint sun tint, so it takes no radiance accumulation below. Test the
                // whole sentinel RANGE, never `==`: a screen-instance id is
                // SDF_SCREEN_MATERIAL + 1 + screenIndex and must never index the material table.
                color = (ScreenGlassColor * (ScreenGlassBase + (ScreenGlassSunTint * sunDiffuse)));
            } else {
                // DETAIL RE-RESOLVE, moved ahead of AO/lighting (Puck.SignedDistance.SdfMaterial's wrap/soften/eye
                // lanes need the resolved material before either): one extra hit-only field evaluation, WITH Detail
                // shapes included, so a rivet or seam's own material wins its footprint. When the host proves
                // there are no Detail shapes, reuse the visibility record's complete attributes and seam instead.
                if (!sdfProgramLayout.noDetailShapes) {
                    sdfDetailShadingActive = true;
                    SdfHit detailHit = mapMasked(surfacePoint, instanceMaskBase);
                    sdfEvalCount += 1.0;
                    sdfDetailShadingActive = false;
                    material = detailHit.material;
                    hitLanes = detailHit.lanes;
                    hitFrameSlot = detailHit.frameSlot;
                    materialBlendWeight = sdfMaterialBlendWeight;
                    materialBlendOther = sdfMaterialBlendOther;
                }

                // MATERIAL BLEND AT SEAMS. The smooth blend eases the DISTANCE across
                // the seam, but `material` is the single integer winner — a hard colour cut at the geometric midpoint.
                // Cross-fade the winner's albedo toward the losing operand captured at the winning smooth blend, by the
                // clamped seam weight (0 at/beyond the blend band, up to 0.5 at the seam centre; symmetric min(h,1-h),
                // so the mix is CONTINUOUS through the winner-flip). HIT-ONLY: one lerp per lit pixel, the channel was
                // already computed by the accept-sample march. Both ids are table materials (the capture zeroes the
                // weight for a screen sentinel) carrying their parityMaterialDelta recolour, so the mixed colour rides
                // the same relaxed material-flip parity family the hard cut already did.
                SdfMaterialData shadeMaterial = sdfMaterialLoad(material);

                if (materialBlendWeight > 0.0) {
                    shadeMaterial.albedo = lerp(shadeMaterial.albedo, sdfMaterialAlbedo(materialBlendOther), materialBlendWeight);
                }

                float3 layerPoint = surfacePoint;
                float3 layerNormal = normal;
                float3 layerRay = rayDirection;
#ifdef SDF_DYNAMIC_TRANSFORMS
                if (hitFrameSlot >= 0) {
                    float3 frameOrigin = sdfDynamicTransforms[3u * (uint)hitFrameSlot].xyz;
                    float4 frameRotation = sdfDynamicTransforms[3u * (uint)hitFrameSlot + 1u];
                    layerPoint = rotatePointByInverseQuaternion(surfacePoint - frameOrigin, frameRotation);
                    layerNormal = rotatePointByInverseQuaternion(normal, frameRotation);
                    layerRay = rotatePointByInverseQuaternion(rayDirection, frameRotation);
                }
#endif
                applyInset(layerPoint, layerNormal, layerRay, shadeMaterial);
                if (shadeMaterial.weathering.x > 0.0 && !curvatureShading) {
                    float unusedMagnitude;
                    calculateNormalCurvature(surfacePoint, instanceMaskBase, terminalRadius, curvature, unusedMagnitude);
                }
                applyWeathering(layerPoint, layerNormal, normal.y, curvature, pixelFootprint * traveled, hitLanes, shadeMaterial);

                // Shading-normal soften (SdfMaterial.Soften): widens the LIT normal toward a wide-stencil field
                // gradient for lighting — AO and the normal debug view use the geometric normal, computed
                // upstream.
                applySoften(normal, surfacePoint, instanceMaskBase, shadeMaterial.soften);

                // 3-tap normal-ladder AO, into the AMBIENT fill ONLY (the sun stays governed by softShadowVisibility above).
                // The ambient pass skips emissive screen cards and initializes neutral AO when world.ao is off.
                float ambientOcclusion = surfaceInfo.ambient;
                // A wrapped (skin-like) material relaxes its ambient fill toward 1 — mix(ao, 1, wrap*.35), the
                // study's boolean skin flag generalized to the continuous wrap lane. wrap = 0 is a no-op.
                ambientOcclusion = lerp(ambientOcclusion, 1.0, saturate(shadeMaterial.wrap * 0.35));

                // Every light in the environment: a directional adds its Lambert term — the shadow light's under its
                // visibility, every other's under ambient occlusion — and a hemisphere its floor-plus-gradient under
                // ambient occlusion. Rim lights are view-dependent and join after the material shade. The env scales
                // dim the directional and ambient families for the room mood. A directional/point Lambert term reads
                // through sdfWrapDiffuse: wrap = 0 reduces it to the plain max(n·l, 0) term exactly.
                float3 radiance = float3(0.0, 0.0, 0.0);
                uint lightCount = worldLightCount();
                int shadowLight = worldShadowLightIndex();

                [loop]
                for (uint lightIndex = 0u; (lightIndex < lightCount); lightIndex++) {
                    SdfEnvLight light = worldLight(lightIndex);

                    if (light.kind == SDF_LIGHT_DIRECTIONAL) {
                        float lambert = sdfWrapDiffuse(dot(normal, light.direction), shadeMaterial.wrap);
                        float occlusion = (((int)lightIndex == shadowLight) ? keyVisibility : ambientOcclusion);

                        radiance += (light.color * (((light.weight * lambert) * occlusion) * sunScale));
                    } else if (light.kind == SDF_LIGHT_HEMISPHERE) {
                        float ambient = (light.weight + (light.param * normal.y));

                        radiance += (light.color * ((ambient * ambientScale) * ambientOcclusion));
                    } else if (light.kind == SDF_LIGHT_POINT) {
                        float3 toLight = (worldPointLightPosition(light) - surfacePoint);
                        float pointDistance = length(toLight);
                        float3 pointDirection = (toLight / max(pointDistance, 1.0e-4));
                        float pointRatio = (pointDistance / max(light.param, 1.0e-3));
                        float pointFalloff = (light.weight / (1.0 + (pointRatio * pointRatio)));
                        float pointLambert = sdfWrapDiffuse(dot(normal, pointDirection), shadeMaterial.wrap);

                        radiance += (light.color * ((pointFalloff * pointLambert) * ambientOcclusion));
                    }
                }

#ifdef SDF_SCREEN_SOURCES
                // Every BOUND diegetic screen is a colored area light: its position/orientation come from the
                // screen-surface table, its color from the per-frame framebuffer average. The dot(screenNormal, -L) gate
                // is the "light through the glass" cue — a screen only lights what sits in front of its face.
                // right/up are orthonormal by contract (SdfScreenSurface); the normalize absorbs upload float drift.
                // The engine-bench sdf.screen-lights lever skips the whole additive loop (the CRTs stop spilling glow).
                if (!worldScreenLightsDisabled()) {
                    for (uint lightIndex = 0u; (lightIndex < screenLightLoopBound()); lightIndex++) {
                        if (!screenSourceBound(lightIndex)) {
                            continue;
                        }

                        ScreenSurfaceData lightSurface = worldScreenSurface(lightIndex);
                        float3 screenNormal = normalize(cross(lightSurface.right.xyz, lightSurface.up.xyz));
                        float3 toLight = (lightSurface.origin.xyz - surfacePoint);
                        float distanceSquared = max(dot(toLight, toLight), ScreenLightMinDistanceSquared);
                        float3 lightDirection = (toLight * rsqrt(distanceSquared));
                        float facing = (max(dot(normal, lightDirection), 0.0) * saturate(dot(screenNormal, -lightDirection)));
                        float attenuation = (1.0 / (1.0 + (ScreenLightFalloff * distanceSquared)));

                        radiance += (sdfScreenLights[lightIndex].rgb * ((sdfScreenLights[lightIndex].a * facing) * attenuation));
                    }
                }
#endif

                color = sdfMaterialShade(shadeMaterial, radiance, normal, rayDirection, worldSunDirection(), sunScale);

                // Warm/cool bounce (SdfMaterial.Bounce): a restrained, art-directed fill on the side of the surface
                // the key (shadow) light does not reach. Black (the default) contributes exactly 0.
                color += ((shadeMaterial.albedo * shadeMaterial.bounce) * ((1.0 - max(dot(normal, keyDirection), 0.0)) * ambientOcclusion));

                // render.environment studio reflections: the horizon gradient plus every authored softbox, sampled
                // about the mirror direction and weighted by the surface's own Fresnel response and ambient occlusion
                // (the same "specular AO" proxy the GGX lobe above has no separate occlusion term for). Zero when the
                // section is unauthored (worldStudioReflection returns exactly 0), so this is a byte-identical no-op
                // addition on an unauthored world.
                {
                    float3 f0 = lerp(float3(shadeMaterial.specular, shadeMaterial.specular, shadeMaterial.specular), shadeMaterial.albedo, shadeMaterial.metal);
                    float3 viewDirection = -rayDirection;
                    float nDotV = saturate(dot(normal, viewDirection));
                    float3 fresnel = (f0 + ((max(float3(1.0, 1.0, 1.0) - shadeMaterial.roughness, f0) - f0) * pow((1.0 - nDotV), 5.0)));
                    float3 reflectDirection = reflect(rayDirection, normal);

                    // No baked gain here: a softbox's authored `weight` and the horizon's authored colors are the
                    // levers (a constant multiplier on top of them would be a tunable baked as a constant).
                    color += ((worldStudioReflection(reflectDirection, shadeMaterial.roughness) * fresnel) * ambientOcclusion);
                }

                // The view-dependent rim lights: an additive silhouette brighten, applied after the material shade
                // because it is a look, not a light the material's specular should answer.
                [loop]
                for (uint rimIndex = 0u; (rimIndex < lightCount); rimIndex++) {
                    SdfEnvLight rim = worldLight(rimIndex);

                    if (rim.kind == SDF_LIGHT_RIM) {
                        color += ((rim.weight * rim.color) * pow((1.0 - saturate(dot(normal, -rayDirection))), rim.param));
                    }
                }
                // Each point light's own GGX specular lobe, from its own direction rather than the shadow light's —
                // the diffuse term above already folded its Lambert contribution into `radiance`. Scaled by ambient
                // occlusion like the diffuse term (no shadow march in v1).
                [loop]
                for (uint pointIndex = 0u; (pointIndex < lightCount); pointIndex++) {
                    SdfEnvLight pointLight = worldLight(pointIndex);

                    if (pointLight.kind == SDF_LIGHT_POINT) {
                        float3 toLight = (worldPointLightPosition(pointLight) - surfacePoint);
                        float pointDistance = length(toLight);
                        float3 pointDirection = (toLight / max(pointDistance, 1.0e-4));
                        float pointRatio = (pointDistance / max(pointLight.param, 1.0e-3));
                        float pointFalloff = (pointLight.weight / (1.0 + (pointRatio * pointRatio)));

                        color += (pointLight.color * sdfMaterialSpecular(shadeMaterial, normal, -rayDirection, pointDirection, (pointFalloff * ambientOcclusion)));
                    }
                }

                // Occluders attenuate reflected light; self-emission remains independent.
                float attenuation = 1.0;
                [loop] for (uint index = 0u; index < lightCount; index++) {
                    SdfEnvLight field = worldLight(index);
                    if (field.kind != SDF_LIGHT_OCCLUDER || field.weight <= 0.0) continue;
                    float3 delta = worldPointLightPosition(field) - surfacePoint;
                    float distanceSquared = dot(delta, delta);
                    float facing = distanceSquared > 1.0e-12 ? saturate(dot(normal, delta * rsqrt(distanceSquared))) : 1.0;
                    float radius = max(field.param, 1.0e-6);
                    attenuation *= 1.0 - saturate(field.weight * exp(-distanceSquared / (radius * radius)) * facing);
                }
                float3 selfEmission = shadeMaterial.albedo * shadeMaterial.emissive;
                color = selfEmission + (color - selfEmission) * attenuation;

                // Stylized curvature enrichment (cavity darken / rim light / ink outline). The compile-time guard strips
                // it (and the extra center tap upstream) from the shipped build on both backends.
                if (curvatureShading) {
                    color = applyCurvatureShading(color, curvature);
                }
            }
        }

        if (useFinalShading) {
#ifdef SDF_SCREEN_SOURCES
            // Grid-lock overlays (grid-locking §4): tint the lit color BEFORE the distance fog so a far grid still
            // recedes. The world grid gates on the surface being the floor plane by HEIGHT (its material id is
            // runtime-assigned, so height is the stable test); the object grid is a finite patch in the reference frame.
            uint gridFlags = passGroup.gridFlags;
            float gridFloorY = passGroup.gridFloorY;

            if (((gridFlags & 1u) != 0u) && (abs(surfacePoint.y - gridFloorY) < 0.02)) {
                color = applyWorldFloorGrid(color, surfacePoint.xz, passGroup.gridWorldPitch, rayDirection, traveled);
            }

            if ((gridFlags & 2u) != 0u) {
                color = applyObjectGrid(color, surfacePoint, rayDirection, gridFloorY);
            }
#endif

            float fog = (1.0 - exp(-worldSkyFogDensity() * traveled));
            color = lerp(color, skyGradient(rayDirection), fog);

            // Approximate sky-silhouette coverage, using visibility from this frame's completed primary pass.
            // A local field rise behind a hit cannot distinguish sky from farther geometry: that old probe
            // painted white halos around grass against the ground and cost another whole-field query.
            // Keep the residual ratio in the same clamped units as hit acceptance; the normal gates grazing hits.
            // Geometry-to-geometry edges receive no sky blend, and a mesh pixel's coverage is zero. Bounded-volume
            // composition still happens afterward.
            float coverage = saturate(terminalRadius / terminalHitThreshold);
            float grazing = (1.0 - saturate(-dot(normal, rayDirection)));
            float edgeWeight = (coverage * grazing);
            bool adjacentSky = false;
            if (edgeWeight > DisplayCode) {
                uint2 renderDims = worldViewDims(view);
                const int2 offsets[4] = { int2(-1, 0), int2(1, 0), int2(0, -1), int2(0, 1) };
                [unroll] for (uint i = 0u; i < 4u; i++) {
                    int2 neighbor = int2(pixel) + offsets[i];
                    if (all(neighbor >= 0) && all(neighbor < int2(renderDims))) {
                        // A neighbour outside the dispatch box has no record from this frame, and the beam proved
                        // its tile empty, so it is sky; inside the box its record is current, empty tiles included.
                        if (!worldVisibilityCurrent(uint2(neighbor))) {
                            adjacentSky = true;
                        } else {
                            SdfVisibility adjacent = sdfLoadVisibility(worldVisibilityRecord(uint2(neighbor), viewIndex));
                            // Exhaustion proves neither sky nor geometry. Treat it conservatively as unknown.
                            adjacentSky = adjacentSky || (!sdfVisibilityHit(adjacent) && sdfVisibilitySteps(adjacent) < (uint)MaxSteps);
                        }
                    }
                }
            }
            if (adjacentSky) {
                color = lerp(color, skyGradient(rayDirection), edgeWeight);
            }
        }
    }

#ifdef SDF_SCREEN_SOURCES
    // Bounded emissive volumes composite last, after the surface/sky color is final, and never paint through solid
    // geometry (clipped to the hit distance, or the far distance on a miss).
    color = shadeVolumes(color, rayOrigin, rayDirection, (hitSurface ? traveled : farDistance), pixel, view.position.w);
#endif

    float3 viewColor = color;

    switch (viewMode) {
        case 1: { // depth
            float depth = saturate(traveled / farDistance);
            viewColor = float3(depth, depth, depth);
            break;
        }
        case 2: { // surface normals
            viewColor = (hitSurface ? ((normal * 0.5) + 0.5) : float3(0.0, 0.0, 0.0));
            break;
        }
        case 3: { // ray direction
            viewColor = ((rayDirection * 0.5) + 0.5);
            break;
        }
        case 4: { // material id palette
            viewColor = (hitSurface ? materialPalette(material) : float3(0.0, 0.0, 0.0));
            break;
        }
        case 5: { // iteration count ramp (after the cull fast-forward — empty tiles read ~0)
            float ramp = (float(marchStep) / float(MaxSteps));
            viewColor = float3(ramp, ramp, ramp);
            break;
        }
        case 6: { // termination cause — WHY the march loop exited, per pixel (reconstructed from post-loop state so
                  // the hot non-debug path's codegen is untouched: no per-step tracking, just a read of the exit facts).
                  // green = epsilon-dominated hit, cyan = footprint-dominated hit (either arm — a closest-approach
                  // candidate accepted at exhaustion classifies by the same dominance test, because it satisfied the
                  // same rule), red = MaxSteps exhausted with no candidate inside the rule (the ground-notch
                  // hypothesis), dark blue = escaped (a validated step past the far distance or the F1 far bound, or a
                  // tile the beam culled empty). A break leaves marchStep below MaxSteps; only exhaustion reaches it.
                  //
                  // THE TERMINATION/SLICE SPLIT (deliberate, keep it): this view shows what the REAL pipeline does —
                  // tile cull included (a beam-culled tile reads as escaped/background here, because that is exactly
                  // what the production march would do). The SLICE view below shows the IDEAL field instead: the beam
                  // force-survives every tile for it and its evaluation is the UNMASKED map(), so no cull or mask can
                  // truncate the picture. One view diagnoses the pipeline, the other the mathematics.
            if (hitSurface) {
                // Which term won the footprint-adaptive threshold at the hit: SurfaceEpsilon (near-camera precision
                // floor) or the pixel's world footprint (pixelFootprint * traveled). Same comparison the loop's
                // max(SurfaceEpsilon, pixelFootprint*traveled) made, read back at the hit distance.
                bool epsilonDominated = (SurfaceEpsilon >= (pixelFootprint * traveled));
                viewColor = (epsilonDominated ? float3(0.15, 0.90, 0.25) : float3(0.15, 0.80, 0.95));
            }
            else if ((marchStart < 0.0) || (marchStep < MaxSteps)) {
                viewColor = float3(0.02, 0.05, 0.28); // escaped to the sky (or a beam-culled empty tile) — background
            }
            else {
                viewColor = float3(0.92, 0.16, 0.10); // the loop ran out of steps without hitting or escaping
            }

            break;
        }
        case 7: { // distance-field cross-section — the IDEAL field, wall to wall (see the termination/slice split
                  // note on case 6). The march was skipped (the gate above); the beam force-survived every in-viewport
                  // tile for this mode, so every pixel of the viewport reaches here — no tile truncation, no staircase.
                  // Default plane: through the WORLD ORIGIN with normal = camera forward (the debug subject sits at
                  // the origin — a camera-locked slice). The pass block's slice axis and offset optionally select a
                  // world-axis plane instead (the `sdf.slice` verb; camera-locked while the axis is 0).
            float3 sliceNormal = view.forward.xyz; // already unit (the camera basis)
            float planeOffset = 0.0;               // the plane is dot(p, n) = planeOffset

            int sliceAxis = (int)round(passGroup.debugSliceAxis);

            if (sliceAxis == 1) { sliceNormal = float3(1.0, 0.0, 0.0); planeOffset = passGroup.debugSliceOffset; }
            else if (sliceAxis == 2) { sliceNormal = float3(0.0, 1.0, 0.0); planeOffset = passGroup.debugSliceOffset; }
            else if (sliceAxis == 3) { sliceNormal = float3(0.0, 0.0, 1.0); planeOffset = passGroup.debugSliceOffset; }

            float denominator = dot(rayDirection, sliceNormal);

            if (abs(denominator) < 1.0e-4) {
                viewColor = float3(0.0, 0.0, 0.0); // ray parallel to the slice — nothing to sample
                break;
            }

            float planeT = ((planeOffset - dot(rayOrigin, sliceNormal)) / denominator);

            if (planeT < 0.0) {
                viewColor = float3(0.0, 0.0, 0.0); // the plane is behind the camera along this ray
                break;
            }

            // The UNMASKED field (map, never mapMasked): the slice is the ideal mathematics, so no per-tile instance mask
            // may hide far-field contributions. Still the post-stepScale-clamp distance — the quantity the marcher steps on — so an isoline IS a level set of the marched field.
            float sliceDistance = mapDistance(rayOrigin + (rayDirection * planeT));

            // Two-scale isolines over the sign-split hue ramp (inside warm/red, outside cool/blue): brightness ramps
            // within each MINOR band (0.25 wu) so the gradient direction stays readable; a thin dark line marks every
            // minor boundary and a heavier, darker line every MAJOR band (1.0 wu), so distance reads at a glance
            // (count the heavy rings, then the light ones). The zero contour stays the one bright white line.
            const float MinorBand = 0.25;
            const float MajorBand = 1.0;
            float fieldMagnitude = abs(sliceDistance);
            float minorPhase = frac(fieldMagnitude / MinorBand);
            float majorPhase = frac(fieldMagnitude / MajorBand);
            float3 field = ((sliceDistance < 0.0) ? float3(0.90, 0.35, 0.22) : float3(0.22, 0.45, 0.90));
            float3 sliceColor = (field * (0.35 + (0.50 * minorPhase)));
            // Distance to the nearest band boundary, in band units (0 at a boundary, 0.5 mid-band).
            float minorEdge = min(minorPhase, (1.0 - minorPhase));
            float majorEdge = min(majorPhase, (1.0 - majorPhase));

            if (minorEdge < 0.05) {  // ~0.0125 wu half-width: thin dark minor line
                sliceColor *= 0.45;
            }

            if (majorEdge < 0.02) {  // ~0.02 wu half-width: heavier, near-black major line
                sliceColor *= 0.15;
            }

            if (fieldMagnitude < 0.02) {
                sliceColor = float3(1.0, 1.0, 1.0); // the bright zero contour — the cross-section outline wins over all
            }

            viewColor = sliceColor;
            break;
        }
        case 8: { // MASK DENSITY — tint by the kept-instance count in this pixel's tile (popcount over the tile's mask
                  // words), normalized by the live instance count. The counts are ALREADY in the mask buffer the views
                  // kernel binds (the beam prepass wrote them), so this is one popcount loop — no march, no field eval.
                  // Cull behaviour and tile-boundary artifacts become visible BY CONSTRUCTION: each tile's density is a
                  // single value, so adjacent tiles that kept different counts show a hard colour step. A world-only
                  // program (0 instances) reads 0 → the floor colour. This is how the lead WATCHES the storm cliff — a
                  // dense red field over the swarm means many instances survive the cull into each tile.
            uint liveInstances = sdfInstanceCount();
            uint keptInstances = 0u;

            [loop]
            for (uint maskWord = 0u; (maskWord < passGroup.instanceMaskWordCount); maskWord++) {
                keptInstances += countbits(sdfInstanceMaskWord(instanceMaskBase, maskWord, liveInstances));
            }

            // Fraction of the live instances this tile keeps. The sqrt lifts the low end so a handful of survivors out of
            // thousands still registers as green rather than washing to the floor blue — the ramp stays perceptible
            // across the whole range while the NORMALIZATION base stays the live count (as specified).
            float density = ((liveInstances > 0u) ? (float(keptInstances) / float(liveInstances)) : 0.0);
            float ramp = sqrt(saturate(density));
            // dark blue (0) -> green (low) -> red (high).
            float3 lowBand = lerp(float3(0.04, 0.07, 0.32), float3(0.14, 0.85, 0.30), saturate(ramp * 2.0));
            viewColor = lerp(lowBand, float3(0.95, 0.16, 0.10), saturate((ramp - 0.5) * 2.0));
            break;
        }
        case 9: { // OVERSHOOT DETECTOR — march the pixel TWICE and colour the depth disagreement. The first march is the
                  // production Lipschitz-CLAMPED field (stepMultiplier 1); the second forces the clamp to 1.0
                  // (stepMultiplier 1/stepScale) so the step rides the raw, possibly-non-1-Lipschitz field and TUNNELS
                  // thin geometry the clamp holds. Where they agree the clamp was not load-bearing (green); where the
                  // unclamped march tunneled past a surface the terminals diverge (hot) — the liar's-spiral class made
                  // live. This is a DEBUG-ONLY two-marches-per-pixel cost; the primary march was gated OFF above for it.
            float clampedDepth = marchOvershootDepth(rayOrigin, rayDirection, marchStart, firstExit, secondEntry, farDistance, instanceMaskBase, pixelFootprint, 1.0);
            float unclampedDepth = marchOvershootDepth(rayOrigin, rayDirection, marchStart, firstExit, secondEntry, farDistance, instanceMaskBase, pixelFootprint, (1.0 / stepScale));
            float disagreement = abs(clampedDepth - unclampedDepth);
            // Log-scaled against the march reach so a sub-unit tunnel still reads while a full escape saturates.
            float hot = saturate(log2(1.0 + disagreement) / log2(1.0 + farDistance));
            // green (agree) -> yellow -> red (the unclamped march tunneled far).
            float3 warmBand = lerp(float3(0.10, 0.70, 0.22), float3(0.98, 0.85, 0.12), saturate(hot * 2.0));
            viewColor = lerp(warmBand, float3(0.96, 0.12, 0.05), saturate((hot - 0.5) * 2.0));
            break;
        }
        case 10: { // EVALS — per-pixel HEATMAP of every map()-family field evaluation tallied this frame (primary
                   // march steps, soft-shadow march steps — regular or fast — the 3/1-tap AO ladder, the analytic-
                   // normal dual or its 4/5-tap fallbacks, and the coverage-AA open-space probe when taken). Unlike
                   // every other numbered mode this one runs the REAL final-shading epilogue (see useFinalShading
                   // above), so sdfEvalCount reflects actual per-frame cost, not a debug shortcut's own cost.
                   // Calibrated ramp (EvalHeatmapCeiling = 256, see its declaration for the worst-case budget this
                   // is sized against): dark blue (idle/background, 0 evals) -> green (a cheap ambient-only hit) ->
                   // yellow (a hit paying the soft-shadow march) -> red (256+, saturating so a runaway pixel reads
                   // solid red instead of wrapping).
            float evalRamp = saturate(sdfEvalCount / EvalHeatmapCeiling);
            float3 coldBand = lerp(float3(0.02, 0.04, 0.20), float3(0.14, 0.85, 0.30), saturate(evalRamp * 2.0));
            viewColor = lerp(coldBand, float3(0.95, 0.16, 0.10), saturate((evalRamp - 0.5) * 2.0));
            break;
        }
        case 11: { // VISIBILITY — the kind of the pixel's current visibility record: background dark blue, SDF green,
                   // mesh orange.
            uint kind = SdfVisibilityKindBackground;

            if (worldVisibilityCurrent(pixel)) {
                kind = sdfVisibilityKind(sdfLoadVisibility(worldVisibilityRecord(pixel, viewIndex)).identity);
            }

            viewColor = ((kind == SdfVisibilityKindSdf)
                ? float3(0.15, 0.90, 0.25)
                : ((kind == SdfVisibilityKindMesh) ? float3(0.95, 0.55, 0.10) : float3(0.02, 0.05, 0.28)));
            break;
        }
    }

    return viewColor;
#endif // SDF_PRIMARY_PASS
}
#endif

#endif
