// The debug views: each numbered mode replaces the view's color with a diagnostic of the pixel's surface sample. Mode 0
// and every mode from SDF_DEBUG_VIEW_COUNT on keep the final shading. The mode indices are DebugViewModes.Names's, generated as SDF_DEBUG_VIEW_*
// (src/Puck.SdfVm/DebugViewModes.cs), whose order is the pass block's debugMode.
#ifndef DEBUG_SDF_DEBUG_VIEWS_HLSLI
#define DEBUG_SDF_DEBUG_VIEWS_HLSLI
#ifdef SDF_VIEWS_PASS
#include "../frame/sdf-reprojection.hlsli"
#include "../shade/sdf-mesh-irradiance.hlsli"

// A distinct, stable hue per material id (an HSV hue ramp), not the table albedo — so id boundaries read clearly
// in the material-id debug view.
float3 materialPalette(int material) {
    float hue = frac(float(material) * 0.61803399);
    float3 ramp = (abs((frac(hue + float3(0.0, 0.33333333, 0.66666667)) * 6.0) - 3.0) - 1.0);

    return saturate(ramp);
}

// The update tier a pixel's rated item carries (SdfUpdateRateCode): a screen's from its ripple record's state row, and a
// surface drawn under a dynamic slot from the slot's position row, where the host writes them while a tier view shows.
uint sdfRateCode(SdfSurfaceSample s) {
    if (!s.hit) {
        return SDF_RATE_NONE;
    }
    if (sdfScreenSurfaceShades(s.material)) {
        return (uint)round(worldScreenRipple((uint)(s.material - SDF_SCREEN_MATERIAL - 1)).state.z);
    }
    if (s.frameSlot != SDF_TRANSFORM_SLOT_NONE) {
        return (uint)round(sdfDynamicTransforms[3u * (uint)s.frameSlot].w);
    }
    return SDF_RATE_NONE;
}
// The tier views: a rated item held to 30 Hz is tinted amber and one held to 15 Hz magenta, over its own shading, with a
// floor of light so a dark surface still shows its tier; rate-flash tints only on the frames the item updated. The tint
// is in nits, so it is scaled by the inverse of the exposure the composite applies.
float3 sdfRateTint(SdfPixel p, SdfSurfaceSample s, float3 color) {
    uint code = sdfRateCode(s);
    uint tier = (code % SDF_RATE_UPDATED);
    bool updated = (code >= SDF_RATE_UPDATED);

    if ((tier < SDF_RATE_HALF) || ((p.viewMode == SDF_DEBUG_VIEW_RATE_FLASH) && !updated)) {
        return color;
    }

    float3 hue = ((tier == SDF_RATE_HALF) ? float3(1.0, 0.6, 0.05) : float3(0.95, 0.1, 0.8));
    float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
    float3 tinted = (hue * (luminance + (0.3 / max(passGroup.exposure, 1.0e-12))));

    return lerp(color, tinted, ((p.viewMode == SDF_DEBUG_VIEW_RATE_FLASH) ? 1.0 : 0.75));
}

float3 sdfDebugView(SdfPixel p, SdfSurfaceSample s, float3 color) {
    float3 viewColor = color;

    switch (p.viewMode) {
        case 1: { // depth
            float depth = saturate(s.t / p.farDistance);
            viewColor = float3(depth, depth, depth);
            break;
        }
        case SDF_DEBUG_VIEW_NORMALS: { // surface normals
            viewColor = (s.hit ? ((s.normal * 0.5) + 0.5) : float3(0.0, 0.0, 0.0));
            break;
        }
        case 3: { // ray direction
            viewColor = ((p.rayDirection * 0.5) + 0.5);
            break;
        }
        case 4: { // material id palette
            viewColor = (s.hit ? materialPalette(s.material) : float3(0.0, 0.0, 0.0));
            break;
        }
        case SDF_DEBUG_VIEW_VISIBILITY: { // the kind of the pixel's visibility record: background dark blue, mesh orange
            viewColor = (s.hit ? float3(0.95, 0.55, 0.10) : float3(0.02, 0.05, 0.28));
            break;
        }
        case SDF_DEBUG_VIEW_MOTION: { // red/green encode previous minus current pixel position at 1/32 per pixel; blue is one
                                    // for valid reprojection. A still surface is (0.5, 0.5, 1), invalid history is black.
            float2 previousPixel;
            float previousT;
            bool valid = sdfReprojection(worldVisibilityRecord(p.pixel, p.viewIndex),
                p.rayOrigin + p.rayDirection * s.t, previousPixel, previousT);
            viewColor = (valid ? float3(saturate(0.5 + ((previousPixel - (float2(p.pixel) + 0.5)) / 32.0)), 1.0) : 0.0);
            break;
        }
        case SDF_DEBUG_VIEW_AO: { // the filtered ambient occlusion views shades with
            viewColor = (s.hit ? s.ambient.xxx : float3(0.0, 0.0, 0.0));
            break;
        }
        case SDF_DEBUG_VIEW_SHADOW: { // the filtered key-light shadow visibility views shades with
            viewColor = (s.hit ? s.shadows.xxx : float3(0.0, 0.0, 0.0));
            break;
        }
        case SDF_DEBUG_VIEW_INDIRECT: { // the irradiance volume's light on a white surface, exposed: a probe artefact
                                        // shows apart from the direct light
            float3 surfacePoint = (p.rayOrigin + (p.rayDirection * s.t));

            viewColor = ((s.hit && sdfMeshIrradianceEnabled()) ? min((sdfMeshIndirect(sdfMeshProbesAt(surfacePoint, s.normal), s.normal) * passGroup.exposure), SDF_LUMINANCE_CEILING) : float3(0.0, 0.0, 0.0));
            break;
        }
        case SDF_DEBUG_VIEW_RATES:
        case SDF_DEBUG_VIEW_RATE_FLASH: { // each rated item tinted by its update tier
            viewColor = sdfRateTint(p, s, color);
            break;
        }
    }

    return viewColor;
}

#endif
#endif
