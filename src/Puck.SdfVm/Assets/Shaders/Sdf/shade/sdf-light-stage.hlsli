// The light stage: shades one pixel from its surface sample (SdfSurfaceSample) into the view's working color, the sky on
// a miss. It samples a bound screen, marches the key light's soft shadow, re-resolves the material, lights the surface,
// and applies the grid overlays, the distance fog and the silhouette coverage; the volumes and the debug views follow it.
#ifndef SHADE_SDF_LIGHT_STAGE_HLSLI
#define SHADE_SDF_LIGHT_STAGE_HLSLI
#ifdef SDF_VIEWS_PASS

// Whether a pixel of `viewMode` takes the final shading. The evals heatmap rides it too, since it tallies what a lit
// pixel really costs.
bool sdfFinalShadingMode(int viewMode) {
    return ((viewMode <= 0) || (viewMode >= DebugViewModeCount) || (viewMode == DebugViewModeEvals));
}

// `groupGather` is the workgroup's shadow candidate decision (sdfShadowGatherGroup): 2 when the group's mask is built, 1
// for the camera-tile mask, 0 for the flat field.
float3 sdfLightStage(SdfPixel p, SdfSurfaceSample s, uint groupGather) {
    float3 color = skyColor(p.rayDirection);

    if (!s.hit) {
        return color;
    }

    float3 surfacePoint = (p.rayOrigin + (p.rayDirection * s.t));
    float3 normal = s.normal;
    float curvature = s.curvature;
    float gradientMagnitude = s.gradientMagnitude;
    int material = s.material;
    float4 hitLanes = s.lanes;
    int hitFrameSlot = s.frameSlot;
    float materialBlendWeight = s.blendWeight;
    int materialBlendOther = s.blendOther;
    bool curvatureShading = worldCurvatureShadingEnabled();
    bool useFinalShading = sdfFinalShadingMode(p.viewMode);
    bool sampledScreen = false;
    // The per-program Lipschitz clamp, which the shadow march divides back out of its world-space comparisons.
    float stepScale = sdfStepScale();

    sdfEvalCount += s.surfaceQueries;

#ifdef SDF_SCREEN_SOURCES
    if (useFinalShading) {
        // A bound screen source wins over both the flat sentinel and the unbound glass: emissive and unlit (the diegetic
        // screen is its own light source, like a real display), but shaped by the CRT glass face before the shared
        // distance fog. The screen also lights the room: see the screen lights below.
        sampledScreen = sampleScreenSurface(material, surfacePoint, p.rayDirection, (p.pixelFootprint * s.t), color);
    }
#endif

    if (useFinalShading && !sampledScreen) {
        // The shadow light's Lambert term under its soft-shadow visibility (the ambient lights still fill shadowed regions,
        // so shadows read soft, not black). The march is skipped where the surface faces away from the light, where no
        // light shadows, on a mesh pixel, or when soft shadows are off (world.shadows off; the light then goes
        // unshadowed). The unbound glass reads sunDiffuse too.
        float3 keyDirection = worldSunDirection();
        float sunDiffuse = max(dot(normal, keyDirection), 0.0);
        float keyVisibility = 1.0;
        // The program's march clamp composed with the hit's local gradient magnitude (GradientMagnitudeFloor): the one
        // de-scale factor the shadow estimate divides by.
        float shadingStepScale = (stepScale * max(gradientMagnitude, GradientMagnitudeFloor));
        // The environment scales dim the room so the diegetic screen glow dominates; the overworld sets them low per frame.
        float ambientScale = passGroup.ambientScale;
        float sunScale = passGroup.sunScale;

        if ((sunDiffuse > 0.0) && (worldShadowLightIndex() >= 0) && !worldSoftShadowsDisabled() && !s.mesh) {
            // One scaled reach for both the gather's cull cone and the march's ceiling (world.shadows's reach), so the
            // gathered occluder set is sound for the shadow ray.
            float shadowReach = (ShadowMaxDistance * worldShadowDistanceScale());
            sdfSecondaryMarchActive = true;
#ifdef SDF_SCREEN_SOURCES
            // The group's decision: 2 marches the group's candidate mask (bit-identical to the flat all-instances march,
            // restricted to the instances the group's shadow rays can reach); 1 the camera-tile mask; 0 the flat field,
            // which is cheap for a few-instance program and keeps the grid toggle render-invariant. The cull off marches
            // the flat field, the ground-truth reference.
            bool cullOn = worldShadowCullEnabled();
            bool culled = (groupGather == 2u);
            uint shadowFallbackMask = ((cullOn && (groupGather == 1u)) ? p.instanceMaskBase : SDF_INSTANCE_MASK_ALL);

            sdfShadowMaskActive = culled;
            // Per-instance soft-shadow participation is live for this march only, in every fallback mode, so a
            // shadow-suppressed dynamic instance drops out of each identically.
            sdfShadowParticipationActive = true;
            keyVisibility = softShadowVisibility(surfacePoint, normal, keyDirection, shadowFallbackMask, shadingStepScale, shadowReach);
            sdfShadowParticipationActive = false;
            sdfShadowMaskActive = false;
#else
            keyVisibility = softShadowVisibility(surfacePoint, normal, keyDirection, p.instanceMaskBase, shadingStepScale, shadowReach);
#endif
            sdfSecondaryMarchActive = false;
            sunDiffuse *= keyVisibility;
        }

        if (material >= SDF_SCREEN_MATERIAL) {
            // The unbound glass: a declared screen with no source bound this frame (or the plain sentinel), unlit apart
            // from a faint sun tint. The whole sentinel range is tested: a screen-instance id is SDF_SCREEN_MATERIAL + 1 +
            // screenIndex and never indexes the material table.
            color = (ScreenGlassColor * (ScreenGlassBase + (ScreenGlassSunTint * sunDiffuse)));
        } else {
            // The detail re-resolve, ahead of the occlusion and the lights, since the material's wrap, soften and eye
            // lanes need the resolved material: one more hit-only field evaluation with the detail shapes included, so a
            // rivet or seam's own material wins its footprint. When the host proves there are no detail shapes, the
            // record's attributes and seam stand. A mesh pixel's surface is its triangle's, never the field's: a textured
            // mesh reads its texel's material, albedo and emission from the atlases.
            bool meshTextured = false;
            SdfMeshTexel meshTexel = (SdfMeshTexel)0;

            if (s.mesh) {
                if (sdfMeshTextured(s.meshDraw)) {
                    meshTextured = true;
                    meshTexel = sdfMeshTexelAt(s.meshDraw, s.meshTriangle, surfacePoint, (p.pixelFootprint * s.t));
                    material = sdfMeshTexelMaterial(s.meshDraw, meshTexel);
                }
            } else if (!sdfProgramLayout.noDetailShapes) {
                sdfDetailShadingActive = true;
                SdfHit detailHit = mapMasked(surfacePoint, p.instanceMaskBase);
                sdfEvalCount += 1.0;
                sdfDetailShadingActive = false;
                material = detailHit.material;
                hitLanes = detailHit.lanes;
                hitFrameSlot = detailHit.frameSlot;
                materialBlendWeight = sdfMaterialBlendWeight;
                materialBlendOther = sdfMaterialBlendOther;
            }

            // The material blend at a seam: the smooth blend eases the distance across it, but the material is one integer
            // winner, so the winner's albedo cross-fades toward the losing operand by the clamped seam weight (0 at and
            // beyond the blend band, up to 0.5 at its centre), continuous through the winner's flip.
            SdfMaterialData shadeMaterial = sdfMaterialLoad(material);

            if (materialBlendWeight > 0.0) {
                shadeMaterial.albedo = lerp(shadeMaterial.albedo, sdfMaterialAlbedo(materialBlendOther), materialBlendWeight);
            }

            // A textured mesh's texel carries its albedo and the light it emits; the emission joins the shade in place of
            // the material's albedo times its emissive strength, which the bake already multiplied.
            float3 meshEmission = float3(0.0, 0.0, 0.0);

            if (meshTextured) {
                shadeMaterial.albedo = sdfMeshTexelAlbedo(meshTexel);
                meshEmission = sdfMeshTexelEmission(meshTexel);
                shadeMaterial.emissive = 0.0;
            }

            float3 layerPoint = surfacePoint;
            float3 layerNormal = normal;
            float3 layerRay = p.rayDirection;
#ifdef SDF_DYNAMIC_TRANSFORMS
            if (hitFrameSlot >= 0) {
                float3 frameOrigin = sdfDynamicTransforms[3u * (uint)hitFrameSlot].xyz;
                float4 frameRotation = sdfDynamicTransforms[3u * (uint)hitFrameSlot + 1u];
                layerPoint = rotatePointByInverseQuaternion(surfacePoint - frameOrigin, frameRotation);
                layerNormal = rotatePointByInverseQuaternion(normal, frameRotation);
                layerRay = rotatePointByInverseQuaternion(p.rayDirection, frameRotation);
            }
#endif
            applyInset(layerPoint, layerNormal, layerRay, shadeMaterial);
            if (shadeMaterial.weathering.x > 0.0 && !curvatureShading) {
                float unusedMagnitude;
                calculateNormalCurvature(surfacePoint, p.instanceMaskBase, s.terminalRadius, curvature, unusedMagnitude);
            }
            applyWeathering(layerPoint, layerNormal, normal.y, curvature, p.pixelFootprint * s.t, hitLanes, shadeMaterial);

            // The shading-normal soften (SdfMaterial.Soften) widens the lit normal toward a wide-stencil field gradient;
            // occlusion and the normal debug view keep the geometric normal.
            applySoften(normal, surfacePoint, p.instanceMaskBase, shadeMaterial.soften);

            // The ambient stage's occlusion, into the ambient fill only (the key light is governed by its soft shadow). A
            // wrapped (skin-like) material relaxes it toward 1; wrap = 0 leaves it as it is.
            float ambientOcclusion = s.ambient;
            ambientOcclusion = lerp(ambientOcclusion, 1.0, saturate(shadeMaterial.wrap * 0.35));

            // Every light in the environment: a directional adds its Lambert term, the shadow light's under its visibility
            // and every other's under ambient occlusion, and a hemisphere its floor-plus-gradient under ambient occlusion.
            // Rim lights are view-dependent and join after the material shade. The environment scales dim the directional
            // and ambient families for the room's mood. A directional or point Lambert term reads through sdfWrapDiffuse:
            // wrap = 0 reduces it to max(n.l, 0).
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
            // Every bound diegetic screen is a colored area light: its position and orientation from the screen-surface
            // table, its color from the frame's average of its image. The dot(screenNormal, -L) gate lights only what sits
            // in front of its face. right and up are orthonormal (SdfScreenSurface); the normalize absorbs upload drift.
            // The sdf.screen-lights lever skips the whole loop.
            if (!worldScreenLightsDisabled()) {
                for (uint screenIndex = 0u; (screenIndex < screenLightLoopBound()); screenIndex++) {
                    if (!screenSourceBound(screenIndex)) {
                        continue;
                    }

                    ScreenSurfaceData lightSurface = worldScreenSurface(screenIndex);
                    float3 screenNormal = normalize(cross(lightSurface.right.xyz, lightSurface.up.xyz));
                    float3 toLight = (lightSurface.origin.xyz - surfacePoint);
                    float distanceSquared = max(dot(toLight, toLight), ScreenLightMinDistanceSquared);
                    float3 lightDirection = (toLight * rsqrt(distanceSquared));
                    float facing = (max(dot(normal, lightDirection), 0.0) * saturate(dot(screenNormal, -lightDirection)));
                    float attenuation = (1.0 / (1.0 + (ScreenLightFalloff * distanceSquared)));

                    radiance += (sdfScreenLights[screenIndex].rgb * ((sdfScreenLights[screenIndex].a * facing) * attenuation));
                }
            }
#endif

            color = (sdfMaterialShade(shadeMaterial, radiance, normal, p.rayDirection, worldSunDirection(), sunScale) + meshEmission);

            // The warm or cool bounce (SdfMaterial.Bounce): a restrained fill on the side of the surface the key light does
            // not reach. Black, the default, adds nothing.
            color += ((shadeMaterial.albedo * shadeMaterial.bounce) * ((1.0 - max(dot(normal, keyDirection), 0.0)) * ambientOcclusion));

            // render.environment's studio reflections: the horizon gradient plus every authored softbox, sampled about the
            // mirror direction and weighted by the surface's Fresnel response and ambient occlusion. An unauthored section
            // adds exactly zero.
            {
                float3 f0 = lerp(float3(shadeMaterial.specular, shadeMaterial.specular, shadeMaterial.specular), shadeMaterial.albedo, shadeMaterial.metal);
                float3 viewDirection = -p.rayDirection;
                float nDotV = saturate(dot(normal, viewDirection));
                float3 fresnel = (f0 + ((max(float3(1.0, 1.0, 1.0) - shadeMaterial.roughness, f0) - f0) * pow((1.0 - nDotV), 5.0)));
                float3 reflectDirection = reflect(p.rayDirection, normal);

                color += ((worldStudioReflection(reflectDirection, shadeMaterial.roughness) * fresnel) * ambientOcclusion);
            }

            // The view-dependent rim lights: an additive silhouette brighten after the material shade, a look rather than a
            // light the material's specular answers.
            [loop]
            for (uint rimIndex = 0u; (rimIndex < lightCount); rimIndex++) {
                SdfEnvLight rim = worldLight(rimIndex);

                if (rim.kind == SDF_LIGHT_RIM) {
                    color += ((rim.weight * rim.color) * pow((1.0 - saturate(dot(normal, -p.rayDirection))), rim.param));
                }
            }
            // Each point light's own GGX specular lobe, from its own direction; its diffuse term is already in the radiance.
            // Scaled by ambient occlusion like the diffuse term.
            [loop]
            for (uint pointIndex = 0u; (pointIndex < lightCount); pointIndex++) {
                SdfEnvLight pointLight = worldLight(pointIndex);

                if (pointLight.kind == SDF_LIGHT_POINT) {
                    float3 toLight = (worldPointLightPosition(pointLight) - surfacePoint);
                    float pointDistance = length(toLight);
                    float3 pointDirection = (toLight / max(pointDistance, 1.0e-4));
                    float pointRatio = (pointDistance / max(pointLight.param, 1.0e-3));
                    float pointFalloff = (pointLight.weight / (1.0 + (pointRatio * pointRatio)));

                    color += (pointLight.color * sdfMaterialSpecular(shadeMaterial, normal, -p.rayDirection, pointDirection, (pointFalloff * ambientOcclusion)));
                }
            }

            // Occluders attenuate reflected light; self-emission stays.
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
            float3 selfEmission = ((shadeMaterial.albedo * shadeMaterial.emissive) + meshEmission);
            color = selfEmission + (color - selfEmission) * attenuation;

            // The stylized curvature terms (cavity darkening, ridge light, ink outline).
            if (curvatureShading) {
                color = applyCurvatureShading(color, curvature);
            }
        }
    }

    if (useFinalShading) {
#ifdef SDF_SCREEN_SOURCES
        // The grid overlays tint the lit color before the distance fog, so a far grid still recedes. The world grid
        // paints the floor plane by height (its material id is assigned at run time); the object grid is a finite patch
        // in the reference frame.
        uint gridFlags = passGroup.gridFlags;
        float gridFloorY = passGroup.gridFloorY;

        if (((gridFlags & 1u) != 0u) && (abs(surfacePoint.y - gridFloorY) < 0.02)) {
            color = applyWorldFloorGrid(color, surfacePoint.xz, passGroup.gridWorldPitch, p.rayDirection, s.t);
        }

        if ((gridFlags & 2u) != 0u) {
            color = applyObjectGrid(color, surfacePoint, p.rayDirection, gridFloorY);
        }
#endif

        float fog = (1.0 - exp(-worldSkyFogDensity() * s.t));
        color = lerp(color, skyGradient(p.rayDirection), fog);

        // The silhouette's sky coverage, from this frame's primary records. The residual ratio stays in the clamped units
        // of hit acceptance, and the normal gates grazing hits. A geometry-to-geometry edge takes no sky blend, and a mesh
        // pixel's coverage is zero.
        float coverage = saturate(s.terminalRadius / s.threshold);
        float grazing = (1.0 - saturate(-dot(normal, p.rayDirection)));
        float edgeWeight = (coverage * grazing);
        bool adjacentSky = false;
        if (edgeWeight > DisplayCode) {
            uint2 renderDims = worldViewDims(p.view);
            const int2 offsets[4] = { int2(-1, 0), int2(1, 0), int2(0, -1), int2(0, 1) };
            [unroll] for (uint i = 0u; i < 4u; i++) {
                int2 neighbor = int2(p.pixel) + offsets[i];
                if (all(neighbor >= 0) && all(neighbor < int2(renderDims))) {
                    // A neighbour outside the dispatch box has no record from this frame, and the beam proved its tile
                    // empty, so it is sky; inside the box its record is current, empty tiles included.
                    if (!worldVisibilityCurrent(uint2(neighbor))) {
                        adjacentSky = true;
                    } else {
                        SdfVisibility adjacent = sdfLoadVisibility(worldVisibilityRecord(uint2(neighbor), p.viewIndex));
                        // Exhaustion proves neither sky nor geometry, so it counts as unknown.
                        adjacentSky = adjacentSky || (!sdfVisibilityHit(adjacent) && sdfVisibilitySteps(adjacent) < (uint)MaxSteps);
                    }
                }
            }
        }
        if (adjacentSky) {
            color = lerp(color, skyGradient(p.rayDirection), edgeWeight);
        }
    }

    return color;
}

#endif
#endif
