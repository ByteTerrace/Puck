// The light stage: shades one pixel from its surface sample (SdfSurfaceSample) into the view's working color, the sky on
// a miss. It samples a bound screen, re-resolves the material, lights the surface through the one light interface
// (sdf-light.hlsli), the key light under the soft shadow the shadow stage wrote, and applies the grid overlays, the
// distance fog and the silhouette coverage; the volumes and the debug views follow it.
#ifndef SHADE_SDF_LIGHT_STAGE_HLSLI
#define SHADE_SDF_LIGHT_STAGE_HLSLI
#include "sdf-light.hlsli"
#ifdef SDF_VIEWS_PASS

float3 sdfLightStage(SdfPixel p, SdfSurfaceSample s) {
    float3 color = skyColor(p.rayDirection);

    if (!s.hit) {
        return color;
    }

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
        // The environment scales dim the room so the diegetic screen glow dominates; the overworld sets them low per frame.
        float ambientScale = passGroup.ambientScale;
        float sunScale = passGroup.sunScale;

        if ((sunDiffuse > 0.0) && (worldShadowLightIndex() >= 0) && !worldSoftShadowsDisabled() && !s.mesh) {
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
            shadeSurface.sunScale = sunScale;
            shadeSurface.ambientScale = ambientScale;

            float3 radiance = float3(0.0, 0.0, 0.0);
            float3 rim = float3(0.0, 0.0, 0.0);
            float3 specular = float3(0.0, 0.0, 0.0);
            float attenuation = 1.0;
            uint lightCount = sdfLightCount();

            [loop]
            for (uint lightIndex = 0u; (lightIndex < lightCount); lightIndex++) {
                SdfLight light;

                if (!sdfLightAt(lightIndex, light)) {
                    continue;
                }

                SdfLightResponse response = sdfLightResponse(light, shadeSurface);

                radiance += response.diffuse;
                rim += response.rim;
                specular += response.specular;
                attenuation *= response.attenuation;
            }

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

            // The rim brightens and the point lights' lobes join after the shade: a rim is a look rather than a light the
            // material's specular answers. The occluders then dim reflected light; self-emission stays.
            color += rim;
            color += specular;

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
