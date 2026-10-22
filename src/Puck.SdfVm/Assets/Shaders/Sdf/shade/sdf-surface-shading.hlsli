// Curvature shading.
#ifndef SHADE_SDF_SURFACE_SHADING_HLSLI
#define SHADE_SDF_SURFACE_SHADING_HLSLI
// Folds the stylized curvature terms into an already-lit, exposed surface color. Every term reads the curvature through
// the authored band: a ridge or cavity saturates at the band's low edge and the ink line spans the band, so a gain is a
// fraction in [0, 1] whatever the geometry's fillet radii (a 0.02-unit fillet has curvature 50; the raw value would
// blow every rounded edge to white). Cavity darkening scales the color down in concavities, the ridge light adds on
// convexities, and the ink outline lerps toward the ink color where the magnitude spikes. `lit` is how brightly the
// surface is lit against SDF_REFERENCE_WHITE, in [0, 1]: the ridge light and the ink color scale by it, so an edge in a
// dim room stays as dim as the walls it joins rather than drawing a light line, or an ink line lighter than them.
float3 applyCurvatureShading(float3 shaded, float curvature, float lit) {
    float low = max(passGroup.curvatureInkLow, 1.0e-3);
    float ridge = smoothstep(0.0, low, max(curvature, 0.0));
    float cavity = smoothstep(0.0, low, max(-curvature, 0.0));

    shaded *= (1.0 - (passGroup.curvatureCavity * cavity));
    shaded += ((passGroup.curvatureRim * lit) * ridge);

    float ink = (passGroup.curvatureInk * smoothstep(low, passGroup.curvatureInkHigh, abs(curvature)));

    return lerp(shaded, (passGroup.curvatureInkColor * lit), saturate(ink));
}

#endif
