// Curvature shading.
#ifndef SHADE_SDF_SURFACE_SHADING_HLSLI
#define SHADE_SDF_SURFACE_SHADING_HLSLI
#ifdef SDF_VIEWS_PASS
// Folds the stylized curvature terms into an already-lit surface color. Every term reads the curvature through the
// authored band: a ridge or cavity saturates at the band's low edge and the ink line spans the band, so a gain is a
// fraction in [0, 1] whatever the geometry's fillet radii (a 0.02-unit fillet has curvature 50; the raw value would
// blow every rounded edge to white). Cavity darkening scales the color down in concavities, the ridge light adds on
// convexities, and the ink outline lerps toward the ink color where the magnitude spikes.
float3 applyCurvatureShading(float3 shaded, float curvature) {
    float low = max((skyFrame[0].CurvatureInkLow), 1.0e-3);
    float ridge = smoothstep(0.0, low, max(curvature, 0.0));
    float cavity = smoothstep(0.0, low, max(-curvature, 0.0));

    shaded *= (1.0 - ((skyFrame[0].CurvatureCavity) * cavity));
    shaded += ((skyFrame[0].CurvatureRim) * ridge);

    // The validated ink interval may be narrower than the ridge/cavity normalization floor.
    float ink = ((skyFrame[0].CurvatureInk) * smoothstep((skyFrame[0].CurvatureInkLow), (skyFrame[0].CurvatureInkHigh), abs(curvature)));

    return lerp(shaded, (skyFrame[0].CurvatureInkColor), saturate(ink));
}

#endif
#endif
