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
    }

    return viewColor;
}

#endif
#endif
