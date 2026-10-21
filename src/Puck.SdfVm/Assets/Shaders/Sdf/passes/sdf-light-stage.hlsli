// The light stage: shades one pixel's hit from its surface sample (SdfSurfaceSample) into the view's lit color, and
// nothing on a miss. It samples a bound screen, loads the material, lights the surface through the one light interface (sdf-light.hlsli), each
// slotted light under its own shadow, adds the irradiance volume's indirect light, applies the editor grid, and reports
// the pixel's coverage: one for a hit, zero on a miss. It also reports the pixel's reactivity for a temporal view's
// resolve: one for a display screen's picture, whose content changes on its own and would trail behind a moving sprite
// through any history (its image is read along the unjittered ray, so its samples agree from frame to frame), zero for
// an opening and for every other surface, emissive ones included: an emission that changes is a shading change the
// resolve's clip shows at once, and a steady one, a power light smaller than a pixel, needs the history the resolve
// gathers over the jitter. The sky, the distance fog and the volumes are the composite's, after this stage; the debug
// views follow it.
#ifndef PASSES_SDF_LIGHT_STAGE_HLSLI
#define PASSES_SDF_LIGHT_STAGE_HLSLI
#include "../shade/sdf-environment.hlsli"
#include "../shade/sdf-surface-shading.hlsli"
#include "../shade/sdf-light.hlsli"
#include "../shade/sdf-grid.hlsli"
#ifdef SDF_VIEWS_PASS
#include "../shade/sdf-mesh-irradiance.hlsli"

// An unbound screen's face: dark glass with nothing behind it, tinted faintly by the sun.
static const float3 ScreenGlassColor = float3(0.02, 0.025, 0.03);
static const float ScreenGlassBase = 0.85;
static const float ScreenGlassSunTint = 0.15;

// The point a screen's image is read at: where the pixel's unjittered ray meets the plane of the face the jittered ray hit,
// so the image a temporal view's reactive screen shows holds still while the jitter moves (the resolve shows a reactive
// pixel's own sample on the unjittered grid).
float3 sdfScreenImagePoint(SdfPixel p, int material, float3 surfacePoint, out float3 rayDirection) {
    rayDirection = p.rayDirection;
    if (passGroup.temporal == 0u) {
        return surfacePoint;
    }

    ViewportData view = p.view;

    view.lens.yz = passGroup.frustumOffset;

    float3 direction = cameraRayDirection(view, ((float2(p.pixel) + 0.5) / float2(worldViewDims(view))));
    ScreenSurfaceData surface = worldScreenSurface((uint)(material - SDF_SCREEN_MATERIAL - 1));
    float3 normal = cross(surface.right.xyz, surface.up.xyz);
    float facing = dot(direction, normal);

    if (abs(facing) < 1.0e-6) {
        return surfacePoint;
    }
    rayDirection = direction;

    return (p.rayOrigin + (direction * (dot((surfacePoint - p.rayOrigin), normal) / facing)));
}

// The stage shades with what the views stage resolved before it (sdfViewsStage): the hit's surface point, whether the
// view shades finally, and the material the pixel shades with.
float3 sdfLightStage(SdfPixel p, SdfSurfaceSample s, float3 surfacePoint, bool useFinalShading, bool curvatureShading, int material, out float coverage, out float reactivity) {
    float3 color = float3(0.0, 0.0, 0.0);

    coverage = 0.0;
    reactivity = 0.0;
    if (!s.hit) {
        return color;
    }
    coverage = 1.0;

    float3 normal = s.normal;
    bool sampledScreen = false;
    reactivity = ((material >= SDF_SCREEN_MATERIAL) ? 1.0 : 0.0);

#ifdef SDF_SCREEN_SOURCES
    if (useFinalShading) {
        // A bound screen source wins over both the flat sentinel and the unbound glass: emissive and unlit (the diegetic
        // screen is its own light source, like a real display), but shaped by the CRT glass face before the shared
        // distance fog. The screen also lights the room: see the screen lights below.
        float3 imageRay;
        float3 imagePoint = sdfScreenImagePoint(p, material, surfacePoint, imageRay);

        float picture;

        sampledScreen = sampleScreenSurface(material, imagePoint, imageRay, (p.pixelFootprint * s.t), color, picture);
        if (sampledScreen) {
            // An opening (a portal's window) is the world beyond its glass, which the resolve's clip keeps current like
            // any other surface; reactive, it would drop every jittered sample's history and flicker at each edge. A
            // display is reactive only over its picture: its bezel band is as steady as the device around it, and keeps
            // its history, so the face's jittered edge resolves like any other edge.
            ScreenMappingData opening = worldScreenMapping((uint)(material - SDF_SCREEN_MATERIAL - 1));

            reactivity = (((opening.imageU.w != 0.0) && (opening.state.w == 0.0)) ? 0.0 : (reactivity * picture));
        }
    }
#endif

    if (useFinalShading && !sampledScreen) {
        // The surface sample's K row, which views filtered (sdfViewsFilter); with shadows off every light goes unshadowed.
        float4 shadowVisibility = float4(1.0, 1.0, 1.0, 1.0);
        float3 keyDirection = worldSunDirection();
        float sunDiffuse = max(dot(normal, keyDirection), 0.0);

        if ((passGroup.shadowSlotCount > 0u) && !worldSoftShadowsDisabled()) {
            shadowVisibility = s.shadows;
        }
        sunDiffuse *= sdfLightVisibility(passGroup.shadowSlots.x, shadowVisibility, 1.0);

        if (material >= SDF_SCREEN_MATERIAL) {
            // The unbound glass: a declared screen with no source bound this frame (or the plain sentinel), unlit apart
            // from a faint sun tint. The whole sentinel range is tested: a screen-instance id is SDF_SCREEN_MATERIAL + 1 +
            // screenIndex and never indexes the material table.
            color = (ScreenGlassColor * (ScreenGlassBase + (ScreenGlassSunTint * sunDiffuse)));
        } else {
            float3 layerPoint = surfacePoint;
            float3 layerNormal = normal;
            float3 layerRay = p.rayDirection;
            if (s.frameSlot != SDF_TRANSFORM_SLOT_NONE) {
                float3 frameOrigin = sdfDynamicTransforms[3u * (uint)s.frameSlot].xyz;
                float4 frameRotation = sdfDynamicTransforms[3u * (uint)s.frameSlot + 1u];
                layerPoint = rotatePointByInverseQuaternion(surfacePoint - frameOrigin, frameRotation);
                layerNormal = rotatePointByInverseQuaternion(normal, frameRotation);
                layerRay = rotatePointByInverseQuaternion(p.rayDirection, frameRotation);
            }
            // The paints draw on the albedo before the rest of the material is loaded, so none of it is live across their
            // shape dispatch.
            uint paintFirst;
            uint paintCount;
            float3 paintedAlbedo = sdfMaterialPaints(material, paintFirst, paintCount);

            sdfApplyPaints(surfacePoint, layerPoint, p.pixelFootprint * s.t, paintFirst, paintCount, paintedAlbedo);

            SdfMaterialData shadeMaterial = sdfMaterialLoad(material);

            shadeMaterial.albedo = paintedAlbedo;
            applyInset(layerPoint, layerNormal, layerRay, shadeMaterial);
            applyWeathering(layerPoint, layerNormal, normal.y, s.curvature, p.pixelFootprint * s.t, s.lanes, shadeMaterial);
            sdfApplyEmissiveLane(s.lanes, shadeMaterial);

            // The surface pass's occlusion, into the ambient fill and unslotted directionals. A wrapped (skin-like)
            // material relaxes it toward 1; wrap = 0 leaves it as it is.
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
            shadeSurface.shadowVisibility = shadowVisibility;

            // The irradiance volume is the indirect light. Its enabled sky source replaces the unoccluded
            // harmonic ambient; disabling that source retains the authored ambient look. Reflections, direct lights and
            // atmosphere remain independent.
            bool meshIndirect = sdfMeshIrradianceEnabled();
            bool indirectSky = (meshIndirect && ((passGroup.indirectSources & SDF_INDIRECT_SOURCES_SKY) != 0u));
            float3 radiance = indirectSky ? 0.0 : worldSkyIrradiance(normal) * ambientOcclusion;
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

            color = sdfMaterialShade(shadeMaterial, radiance, normal, p.rayDirection, worldSunDirection(), 1.0);

            if (meshIndirect) {
                color += sdfMeshIndirect(surfacePoint, normal) * shadeMaterial.albedo
                    * passGroup.indirectApply.rgb
                    * ((1.0 - shadeMaterial.metal) * shadeMaterial.receive * passGroup.indirectApply.w
                        * lerp(1.0, ambientOcclusion, passGroup.indirectContact));
            }
            // Authored fill is the indirect-off look. It never adds a second approximation while the volume is on.
            if (!meshIndirect) {
                color += ((shadeMaterial.albedo * shadeMaterial.fill) * ((1.0 - max(dot(normal, keyDirection), 0.0)) * ambientOcclusion));
            }

            // The lighting-visible sky in the mirror direction, under Fresnel and local occlusion.
            {
                float3 f0 = lerp(float3(shadeMaterial.specular, shadeMaterial.specular, shadeMaterial.specular), shadeMaterial.albedo, shadeMaterial.metal);
                float3 viewDirection = -p.rayDirection;
                float nDotV = saturate(dot(normal, viewDirection));
                float3 fresnel = (f0 + ((max(float3(1.0, 1.0, 1.0) - shadeMaterial.roughness, f0) - f0) * pow((1.0 - nDotV), 5.0)));
                float3 reflectDirection = reflect(p.rayDirection, normal);

                color += ((worldEnvironmentReflection(reflectDirection, shadeMaterial.roughness) * fresnel) * ambientOcclusion);
            }

            // The rim brightens and the point lights' lobes join after the shade: a rim is a look rather than a light the
            // material's specular answers. The occluders then dim reflected light; self-emission stays.
            color += rim;
            color += specular;

            float3 selfEmission = (shadeMaterial.albedo * shadeMaterial.emissive);
            color = selfEmission + (color - selfEmission) * attenuation;

            // The stylized curvature terms (cavity darkening, ridge light, ink outline).
            if (curvatureShading) {
                color = applyCurvatureShading(color, s.curvature);
            }
        }
    }

    if (useFinalShading) {
        // The editor grid tints the lit color before the composite's distance fog, so a far grid still recedes. It reads
        // the geometric normal.
        color = sdfApplyGrid(color, surfacePoint, s.normal, p.rayDirection, (p.pixelFootprint * s.t));
    }

    return color;
}

#endif
#endif
