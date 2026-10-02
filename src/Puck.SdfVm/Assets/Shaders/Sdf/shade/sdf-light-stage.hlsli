// The light stage: shades one pixel's hit from its surface sample (SdfSurfaceSample) into the view's lit color, and
// nothing on a miss. It samples a bound screen, re-resolves the material, lights the surface through the one light
// interface (sdf-light.hlsli), the key light under the soft shadow the shadow stage wrote, applies the editor grid, and
// reports the pixel's coverage: one for a solid hit, less on a silhouette edge against the sky, zero on a miss. It also
// reports the pixel's reactivity for a temporal view's resolve: one for a screen, whose content changes on its own, and
// for emission, which the material model cannot tell animated from steady, the share of the color the material and atlas
// texel that actually shaded the pixel emit. The sky, the distance fog and the volumes are the composite's, after this
// stage; the debug views follow it.
#ifndef SHADE_SDF_LIGHT_STAGE_HLSLI
#define SHADE_SDF_LIGHT_STAGE_HLSLI
#include "sdf-light.hlsli"
#include "sdf-grid.hlsli"
#ifdef SDF_VIEWS_PASS

float3 sdfLightStage(SdfPixel p, SdfSurfaceSample s, out float coverage, out float reactivity) {
    float3 color = float3(0.0, 0.0, 0.0);
    float3 emission = float3(0.0, 0.0, 0.0);

    coverage = 0.0;
    reactivity = 0.0;
    if (!s.hit) {
        return color;
    }
    coverage = 1.0;

    float3 surfacePoint = (p.rayOrigin + (p.rayDirection * s.t));
    float3 normal = s.normal;
    float curvature = s.curvature;
    int material = s.material;
    float4 hitLanes = s.lanes;
    int hitFrameSlot = s.frameSlot;
    float materialBlendWeight = s.blendWeight;
    int materialBlendOther = s.blendOther;
    bool curvatureShading = worldCurvatureShadingEnabled();
    bool useFinalShading = worldFinalShadingMode(p.viewMode);
    bool sampledScreen = false;
    reactivity = ((material >= SDF_SCREEN_MATERIAL) ? 1.0 : 0.0);

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
        // The shadow light's Lambert term under the soft-shadow visibility the shadow stage wrote (the ambient lights still
        // fill shadowed regions, so shadows read soft, not black). The record's key row is current exactly where the
        // shadow stage marched: where the surface faces the light, a light shadows, soft shadows are on, and the pixel is
        // no mesh's. The unbound glass reads sunDiffuse too.
        float3 keyDirection = worldSunDirection();
        float sunDiffuse = max(dot(normal, keyDirection), 0.0);
        float keyVisibility = 1.0;

        if ((sunDiffuse > 0.0) && (passGroup.shadowLight >= 0) && !worldSoftShadowsDisabled() && !s.mesh) {
            keyVisibility = s.keyVisibility;
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
            bool meshImpostor = false;
            SdfMeshTexel meshTexel = (SdfMeshTexel)0;
            SdfImpostorSurface impostorSurface = (SdfImpostorSurface)0;

            if (s.mesh) {
                if (sdfMeshIsImpostor(s.meshDraw)) {
                    meshImpostor = true;
                    impostorSurface = sdfImpostorSurfaceAt(s.meshDraw, p.rayOrigin, p.rayDirection, s.t, p.pixelFootprint);
                } else if (sdfMeshTextured(s.meshDraw)) {
                    meshTextured = true;
                    meshTexel = sdfMeshTexelAt(s.meshDraw, s.meshTriangle, surfacePoint, (p.pixelFootprint * s.t));
                    material = sdfMeshTexelMaterial(s.meshDraw, meshTexel);
                }
            } else if (!sdfProgramLayout.noDetailShapes) {
                sdfDetailShadingActive = true;
                SdfHit detailHit = mapMasked(surfacePoint, p.instanceMaskBase);
                sdfEvalCount += 1.0;
                sdfWorkSteps += 1u;
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
            } else if (meshImpostor) {
                shadeMaterial.albedo = impostorSurface.albedo;
                meshEmission = impostorSurface.emission;
                shadeMaterial.emissive = 0.0;
            }

            float3 layerPoint = surfacePoint;
            float3 layerNormal = normal;
            float3 layerRay = p.rayDirection;
#ifdef SDF_DYNAMIC_TRANSFORMS
            if (hitFrameSlot != SDF_TRANSFORM_SLOT_NONE) {
                float3 frameOrigin = sdfDynamicTransforms[3u * (uint)hitFrameSlot].xyz;
                float4 frameRotation = sdfDynamicTransforms[3u * (uint)hitFrameSlot + 1u];
                layerPoint = rotatePointByInverseQuaternion(surfacePoint - frameOrigin, frameRotation);
                layerNormal = rotatePointByInverseQuaternion(normal, frameRotation);
                layerRay = rotatePointByInverseQuaternion(p.rayDirection, frameRotation);
            }
#endif
            applyInset(layerPoint, layerNormal, layerRay, shadeMaterial);

            // The weathering's curvature (when the surface stage did not measure it) and the soften's wide-stencil
            // gradient come from one probe call, so the kernel inlines the interpreter once for both.
            bool weatheringCurvature = ((shadeMaterial.weathering.x > 0.0) && !curvatureShading);
            bool softens = !(shadeMaterial.soften <= 0.0);
            SdfFieldProbes probes = (SdfFieldProbes)0;

            if (weatheringCurvature || softens) {
                bool centerTap = (weatheringCurvature && !sdfProgramLayout.noDetailShapes);

                probes = sdfProbeField(surfacePoint, p.instanceMaskBase, weatheringCurvature, centerTap, softens);

                if (weatheringCurvature) {
                    float center = s.terminalRadius;

                    sdfEvalCount += 4.0;
                    sdfWorkSteps += 4u;

                    if (centerTap) {
                        center = probes.center;
                        sdfEvalCount += 1.0;
                        sdfWorkSteps += 1u;
                    }

                    curvature = sdfProbeCurvature(probes, center);
                }
            }

            applyWeathering(layerPoint, layerNormal, normal.y, curvature, p.pixelFootprint * s.t, hitLanes, shadeMaterial);

            // The shading-normal soften (SdfMaterial.Soften) widens the lit normal toward a wide-stencil field gradient;
            // occlusion and the normal debug view keep the geometric normal.
            applySoften(normal, probes.softenSum, shadeMaterial.soften);

            // The ambient stage's occlusion, into the ambient fill only (the key light is governed by its soft shadow). A
            // wrapped (skin-like) material relaxes it toward 1; wrap = 0 leaves it as it is.
            float ambientOcclusion = s.ambient;
            ambientOcclusion = lerp(ambientOcclusion, 1.0, saturate(shadeMaterial.wrap * 0.35));

            // Every light answers through the one interface (sdfLightResponse): its diffuse term joins the radiance the
            // material shade lights by, and its rim and specular terms and its attenuation of reflected light follow the
            // shade.
            SdfShadeSurface shadeSurface;
            shadeSurface.position = surfacePoint;
            shadeSurface.normal = normal;
            shadeSurface.rayDirection = p.rayDirection;
            shadeSurface.material = shadeMaterial;
            shadeSurface.ambientOcclusion = ambientOcclusion;
            shadeSurface.keyVisibility = keyVisibility;

            float3 radiance = float3(0.0, 0.0, 0.0);
            float3 rim = float3(0.0, 0.0, 0.0);
            float3 specular = float3(0.0, 0.0, 0.0);
            float attenuation = 1.0;
            uint lightCount = sdfLightCount();

            [loop]
            for (uint lightIndex = 0u; (lightIndex < lightCount); lightIndex++) {
                SdfLightSource light;

                if (!sdfLightAt(lightIndex, light)) {
                    continue;
                }

                SdfLightResponse response = sdfLightResponse(light, shadeSurface);

                radiance += response.diffuse;
                rim += response.rim;
                specular += response.specular;
                attenuation *= response.attenuation;
            }

            color = (sdfMaterialShade(shadeMaterial, radiance, normal, p.rayDirection, worldSunDirection(), 1.0) + meshEmission);

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

            // The rim brightens and the point lights' lobes join after the shade: a rim is a look rather than a light the
            // material's specular answers. The occluders then dim reflected light; self-emission stays.
            color += rim;
            color += specular;

            float3 selfEmission = ((shadeMaterial.albedo * shadeMaterial.emissive) + meshEmission);
            color = selfEmission + (color - selfEmission) * attenuation;
            // Use the material and atlas texel that actually shaded this pixel, after detail re-resolution and layers.
            emission = selfEmission;

            // The stylized curvature terms (cavity darkening, ridge light, ink outline).
            if (curvatureShading) {
                color = applyCurvatureShading(color, curvature);
            }
        }
    }

    if (useFinalShading) {
        // The editor grid tints the lit color before the composite's distance fog, so a far grid still recedes. It reads
        // the geometric normal, which the soften above never widens.
        color = sdfApplyGrid(color, surfacePoint, s.normal, p.rayDirection, (p.pixelFootprint * s.t));

        // The silhouette's sky coverage, from this frame's primary records. The residual ratio stays in the clamped units
        // of hit acceptance, and the normal gates grazing hits. A geometry-to-geometry edge stays wholly covered, and a mesh
        // pixel's residual is zero.
        float residual = saturate(s.terminalRadius / s.threshold);
        float grazing = (1.0 - saturate(-dot(normal, p.rayDirection)));
        float edgeWeight = (residual * grazing);
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
        // The sky's share of an edge beside sky is what the hit does not cover: the composite blends the full sky there.
        if (adjacentSky) {
            coverage = (1.0 - edgeWeight);
        }
    }

    static const float3 Luma = float3(0.2126, 0.7152, 0.0722);
    reactivity = max(reactivity, saturate(dot(emission, Luma) / max(dot(color, Luma), 1.0e-4)));
    return color;
}

#endif
#endif
