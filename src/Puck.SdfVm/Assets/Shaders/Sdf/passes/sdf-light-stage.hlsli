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
#include "../isa/sdf-shade.hlsli"
#include "../shade/sdf-grid.hlsli"
#include "../shade/sdf-toon-outline.hlsli"
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
    if ((passGroup.temporal == 0u) || !sdfScreenSurfaceShades(material)) {
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
        // screen is its own light source, like a real display), drawn through its screen material before the shared
        // distance fog. The screen also lights the room: see the screen lights below.
        float3 imageRay;
        float3 imagePoint = sdfScreenImagePoint(p, material, surfacePoint, imageRay);

        float picture;

        sampledScreen = sampleScreenSurface(material, imagePoint, imageRay, (p.pixelFootprint * s.t), color, picture);
        if (sampledScreen) {
            // An opening (a portal's window) is the world beyond its glass, which the resolve's clip keeps current like
            // any other surface; reactive, it would drop every jittered sample's history and flicker at each edge. A
            // display is reactive only over its picture: its edge band (the face's outermost pixel, and a screen material's soft edge)
            // is as steady as the device around it, and keeps its history, so the face's jittered edge resolves like any
            // other edge. An opening that ripples is the one
            // reactive opening: its refraction moves on its own, and a history would smear it.
            uint screenIndex = (uint)(material - SDF_SCREEN_MATERIAL - 1);
            ScreenMappingData opening = worldScreenMapping(screenIndex);
            bool isOpening = ((opening.imageU.w != 0.0) && (opening.state.w == 0.0));
            bool ripples = (isOpening && (worldScreenRipple(screenIndex).state.x > 0.0));

            reactivity = (ripples ? 1.0 : (isOpening ? 0.0 : (reactivity * picture)));
            // A picture value of one shows at the screen's luminance in nits. An opening's image is the luminance
            // beyond it raised by the view beyond's exposure to the window share (SDF_WINDOW_ADAPTATION, the composite of
            // every window view), so this view applies the rest of its own exposure to it: the window shows the world
            // beyond at an exposure between its own and this view's. The view's screen fidelity eases the scale to the
            // value that leaves the composite's exposure as the picture itself, so a framed screen shows its source
            // exactly.
            float scale = (isOpening ? pow(max(passGroup.exposure, 1.0e-12), -SDF_WINDOW_ADAPTATION) : opening.state.z);

            color *= lerp(scale, (1.0 / max(passGroup.exposure, 1.0e-12)), saturate(passGroup.screenFidelity));
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
            // from a faint sun tint, display-referred: the composite's exposure leaves it as this color. The whole
            // sentinel range is tested: a screen-instance id is SDF_SCREEN_MATERIAL + 1 + screenIndex and never indexes
            // the material table.
            color = ((ScreenGlassColor * (ScreenGlassBase + (ScreenGlassSunTint * sunDiffuse))) / max(passGroup.exposure, 1.0e-12));
        } else {
            float3 layerPoint;
            float3 layerNormal;
            float3 layerRay;
            float4 frameRotation;
            sdfLayerFrame(s.frameSlot, surfacePoint, normal, p.rayDirection, layerPoint, layerNormal, layerRay, frameRotation);
            // The paints draw on the albedo before the rest of the material is loaded, so none of it is live across their
            // shape dispatch.
            uint paintFirst;
            uint paintCount;
            float3 paintedAlbedo = sdfMaterialPaints(material, paintFirst, paintCount);
            SdfShadeBinding shadeBinding = sdfShadeBinding(material);
            float paintCoverage;
            bool deferPaint = (sdfShadeReadsOf(shadeBinding) & SDF_SHADE_READ_PAINT) != 0u;
            sdfApplyPaints(surfacePoint, layerPoint, p.pixelFootprint * s.t, paintFirst, paintCount, deferPaint, paintedAlbedo, paintCoverage);

            SdfMaterialData shadeMaterial = sdfMaterialLoad(material);

            shadeMaterial.albedo = paintedAlbedo;
            SdfShadeStamp shadeStamp = sdfShadeStamp(shadeMaterial, surfacePoint, layerPoint, normal, p.rayDirection,
                s.curvature, p.pixelFootprint * s.t, s.lanes, 0.0, 0.0, frameRotation, s.frameSlot, s.meshDraw);
            sdfShadeSet_paint_coverage(shadeStamp, paintCoverage);
            float revealed;
            if (sdfShadeBound(shadeBinding)) {
                sdfShadeSurfacePhase(shadeBinding, shadeStamp, shadeMaterial, normal, revealed);
            } else {
            applyInset(layerPoint, layerNormal, layerRay, shadeMaterial);
            applyWeathering(layerPoint, layerNormal, normal.y, s.curvature, p.pixelFootprint * s.t, s.lanes, shadeMaterial);
            normal = sdfShadeFlow(surfacePoint, normal, layerPoint, layerNormal, frameRotation, s.frameSlot, s.meshDraw, p.pixelFootprint * s.t,
                s.lanes, 0.0, shadeMaterial, revealed);
            }
            sdfApplyEmissiveLane(s.lanes, shadeMaterial);
            sdfShadeSurfaceInputs(shadeStamp, shadeMaterial, normal, revealed);
            float4 shadowMeasured = float4(1.0, 1.0, 1.0, 1.0);
            if (sdfShadeBound(shadeBinding) && !worldSoftShadowsDisabled()) {
                uint shadowWord = sdfVisibilityRecordBuffer[worldVisibilityRecord(p.pixel, p.viewIndex) + SdfVisibilityShadowWord];
                shadowMeasured = sdfShadowQueried(shadowWord);
            }

            // The surface pass's occlusion, into the ambient fill and unslotted directionals. A wrapped (skin-like)
            // material relaxes it toward 1; wrap = 0 leaves it as it is.
            float ambientOcclusion = s.ambient;
            ambientOcclusion = lerp(ambientOcclusion, 1.0, saturate(shadeMaterial.wrap * 0.35));

            // The irradiance volume is the indirect light, gathered once about the surface: its enabled sky source
            // replaces the unoccluded harmonic ambient (disabling that source retains the authored ambient look), its
            // probes occlude the traced point and spot lights and the reflected environment, and a surface no probe has
            // measured yet reads the camera sample's open sky (sdfMeshAnswers). Direct lights and atmosphere remain
            // independent.
            bool meshIndirect = sdfMeshIrradianceEnabled();
            bool indirectSky = (meshIndirect && ((passGroup.indirectSources & SDF_INDIRECT_SOURCES_SKY) != 0u));
            SdfMeshProbes probes = sdfMeshProbesAt(surfacePoint, normal);

            // Every light answers through the one interface (sdfLightResponse): its diffuse term joins the radiance the
            // material shade lights by, and its rim and specular terms and its attenuation of reflected light follow the
            // shade.
            SdfShadeSurface shadeSurface;
            shadeSurface.position = surfacePoint;
            shadeSurface.normal = normal;
            shadeSurface.rayDirection = p.rayDirection;
            shadeSurface.material = shadeMaterial;
            shadeSurface.toon = sdfToonStyle(shadeMaterial);
            if (sdfShadeBound(shadeBinding)) shadeSurface.toon = (SdfToonStyle)0;
            shadeSurface.ambientOcclusion = ambientOcclusion;
            shadeSurface.shadowVisibility = shadowVisibility;
            shadeSurface.shadowMeasured = shadowMeasured;
            shadeSurface.cascaded = ((passGroup.meshShadowCount > 0u) && !worldSoftShadowsDisabled());
            shadeSurface.probed = (meshIndirect && sdfMeshAnswers(probes));
            shadeSurface.probes = probes;

            float3 radiance = indirectSky ? 0.0 : worldSkyIrradiance(normal) * ambientOcclusion;
            float3 ambientTerm = radiance;
            SdfToonStyle style = sdfToonStyle(shadeMaterial);
            bool toon = !sdfShadeBound(shadeBinding) && sdfToonEnabled(style);
            if (toon) { radiance *= style.shadow.rgb; }
            float3 rim = float3(0.0, 0.0, 0.0);
            float3 specular = float3(0.0, 0.0, 0.0);
            float attenuation = 1.0;
            SdfLightWalk walk = sdfLightWalkStart();
            SdfLightSource light;

            [loop]
            while (sdfLightNext(walk, light)) {
                SdfLightResponse response = sdfShadeLightPhase(shadeBinding, shadeStamp, light, shadeSurface);

                radiance += response.diffuse;
                rim += response.rim;
                specular += response.specular;
                attenuation *= response.attenuation;
            }

            color = sdfMaterialShade(shadeMaterial, radiance, normal, p.rayDirection);
            if (toon) {
                // The banded lights shade the paint flat, metals included; the environment lobe, the rim and the bounded
                // highlights join after it as for any material.
                color = shadeMaterial.albedo * radiance;
            }

            float3 indirect = float3(0.0, 0.0, 0.0);
            float3 indirectTerm = 0.0;

            if (meshIndirect) {
                indirect = sdfMeshIndirectSeen(probes, normal);
                if (toon) { indirect *= style.shadow.rgb; }
                indirectTerm = indirect * passGroup.indirectApply.rgb * (shadeMaterial.receive * passGroup.indirectApply.w
                    * lerp(1.0, ambientOcclusion, passGroup.indirectContact));
                color += indirectTerm * shadeMaterial.albedo * (toon ? 1.0 : (1.0 - shadeMaterial.metal));
            }
            // Authored fill is the indirect-off look: a share of the key light's luminance on a white surface. It never
            // adds a second approximation while the volume is on.
            if (!meshIndirect) {
                float fill = toon ? 1.0 - sdfToonStep(style, dot(normal, keyDirection))
                    : 1.0 - max(dot(normal, keyDirection), 0.0);
                float3 fillTint = toon ? style.shadow.rgb : 1.0;
                color += shadeMaterial.albedo * shadeMaterial.fill * worldKeyRadiance() * fill * ambientOcclusion * fillTint;
            }

            // The environment the specular lobe reflects, for a material with a specular response: the lighting-visible
            // sky about the mirror direction, blurred toward its irradiance as the lobe widens with roughness, under the
            // lobe's directional albedo (sdfMaterialEnvironmentBrdf, which a rough dielectric keeps near its f0 at
            // every angle, so matte paint reflects little of the sky even at grazing) and local occlusion; with the
            // irradiance volume on, only as far as the volume sees the sky open that way, and the surroundings' light
            // where not.
            float3 environmentTerm = 0.0;
            float3 reflectionTerm = 0.0;
            if ((shadeMaterial.specular > 0.0) || (shadeMaterial.metal > 0.0)) {
                float3 f0 = lerp(float3(shadeMaterial.specular, shadeMaterial.specular, shadeMaterial.specular), shadeMaterial.albedo, shadeMaterial.metal);
                float3 viewDirection = -p.rayDirection;
                float nDotV = saturate(dot(normal, viewDirection));
                float3 reflectDirection = reflect(p.rayDirection, normal);
                float3 environment = worldEnvironmentReflection(reflectDirection, shadeMaterial.roughness);
                environmentTerm = environment;

                if (meshIndirect) {
                    environment = sdfMeshReflectionSeen(probes, reflectDirection, environment);
                }
                reflectionTerm = environment;
                environmentTerm = ((environment * sdfMaterialEnvironmentBrdf(f0, shadeMaterial.roughness, nDotV)) * ambientOcclusion);
                color += environmentTerm;
            }

            // The rim brightens and the point lights' lobes join after the shade: a rim is a look rather than a light the
            // material's specular answers. The occluders then dim reflected light; self-emission stays.
            color += rim;
            color += specular;

            float3 selfEmission = (shadeMaterial.albedo * shadeMaterial.emissive);
            color = toon ? color * attenuation : selfEmission + (color - selfEmission) * attenuation;

            // The local adaptation (SdfExposure.Local): the surface's exposure moves that share of the way from the
            // view's toward the one its own illumination would meter, the light arriving at it (the lights' diffuse
            // terms and the volume's, what a white surface here would show) against the view's meter reading taken as
            // a scene of SDF_LOCAL_REFLECTANCE albedo, within SDF_LOCAL_ADAPTATION_RANGE stops: a dim ceiling lifts
            // and a sunlit floor compresses, while a dark paint under the same light stays dark. It precedes the
            // display-referred terms below.
            float incoming = dot((radiance + indirect), SDF_LUMINANCE);
            float localGain = sdfLocalAdaptationGain(incoming);

            color *= localGain;
            color = sdfShadeComposePhase(shadeBinding, shadeStamp, radiance - ambientTerm, rim, specular, attenuation,
                indirectTerm + ambientTerm, reflectionTerm, environmentTerm, localGain, color);

            // The stylized curvature terms (cavity darkening, ridge light, ink outline) apply to the color the view's
            // exposure shows, at the strength the surface's exposed illumination reaches against a white surface the
            // meter aims for: a lit room keeps its edge highlights and ink, and a dim room's edges stay dim.
            if (toon) {
                color = sdfToonExpose(style, color);
                float ink = sdfToonOutline(style, p, s, surfacePoint);
                color *= lerp(1.0, style.ink, ink);
                color += selfEmission;
            } else if (sdfShadeBound(shadeBinding) && sdfToonEnabled(style)) {
                color *= lerp(1.0, style.ink, sdfToonOutline(style, p, s, surfacePoint));
            } else if (curvatureShading) {
                float lit = saturate(((incoming * localGain) * passGroup.exposure) / SDF_REFERENCE_WHITE);

                color = (applyCurvatureShading((color * passGroup.exposure), s.curvature, lit) / max(passGroup.exposure, 1.0e-12));
            }
        }
    }

    if (useFinalShading) {
        // The editor grid tints the lit color before the composite's distance fog, so a far grid still recedes. It reads
        // the geometric normal, and tints the color the view's exposure shows.
        color = (sdfApplyGrid((color * passGroup.exposure), surfacePoint, s.normal, p.rayDirection, (p.pixelFootprint * s.t)) / max(passGroup.exposure, 1.0e-12));
    }

    return color;
}

#endif
#endif
