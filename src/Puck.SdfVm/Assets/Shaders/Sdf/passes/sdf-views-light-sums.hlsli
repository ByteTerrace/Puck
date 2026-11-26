// The light sums (SdfWorldPackage.ViewsLightSums): what a binned view's light pass (sdf-world-views-lights.comp.hlsl)
// walked into each pixel it shades, which the binned views kernels read in place of a walk of their own, so the walk's
// registers and its loads of the lights table and the probe slots are the light pass's alone. One uint4 a render-extent
// pixel, addressed as the reactivity is (sdfReactivityIndex): in x, y and z the lights' diffuse terms, their rim terms and
// their specular lobes, each a shared-exponent colour (Shared/shared-exponent.hlsli) at the view's stored light scale
// (passGroup.lightScale, which keeps a noon sky and a star's glint within the format as it keeps them within the lit
// image's half floats); in w the product of the occluders' attenuations in its low half and the largest line any light
// phase raised in its high half, each UNORM16.
//
// The invariant: the views kernels read a pixel's sums exactly where the light pass stored them, a hit the light stage
// lights (sdfLightStage: a final shading of a material below SDF_SCREEN_MATERIAL), at a pixel the views rate shades. Both
// passes decide that from the same visibility record, pass block and rate tiles, through one stage (sdfViewsStage), and
// the graph orders the light pass before views; nothing else holds the two to one rule.
#ifndef PASSES_SDF_VIEWS_LIGHT_SUMS_HLSLI
#define PASSES_SDF_VIEWS_LIGHT_SUMS_HLSLI
#include "Shared/shared-exponent.hlsli"

// What a pixel's light walk added: sdfLightStage's sums over the walk.
struct SdfViewsLightSum {
    float3 diffuse;
    float3 rim;
    float3 specular;
    float attenuation;
    float linework;
};

#if defined(SDF_VIEWS_LIGHT_PASS)
// Stores a pixel's sums, in the light pass alone.
void sdfViewsStoreLightSum(uint2 pixel, uint viewIndex, SdfViewsLightSum sum) {
    float scale = max(passGroup.lightScale, 1.0e-30);

    sdfViewsLightSumsRW[sdfReactivityIndex(pixel, viewIndex, passGroup.imageExtent)] = uint4(
        puckPackSharedExponent(sum.diffuse * scale),
        puckPackSharedExponent(sum.rim * scale),
        puckPackSharedExponent(sum.specular * scale),
        ((uint)round(saturate(sum.attenuation) * 65535.0) | ((uint)round(saturate(sum.linework) * 65535.0) << 16u)));
}
#elif defined(SDF_VIEWS_BINNED)
// Reads a pixel's sums, in a binned views kernel.
SdfViewsLightSum sdfViewsLightSumAt(uint2 pixel, uint viewIndex) {
    uint4 words = sdfViewsLightSums[sdfReactivityIndex(pixel, viewIndex, passGroup.imageExtent)];
    float unscale = (1.0 / max(passGroup.lightScale, 1.0e-30));
    SdfViewsLightSum sum;

    sum.diffuse = (puckUnpackSharedExponent(words.x) * unscale);
    sum.rim = (puckUnpackSharedExponent(words.y) * unscale);
    sum.specular = (puckUnpackSharedExponent(words.z) * unscale);
    sum.attenuation = (float(words.w & 0xFFFFu) / 65535.0);
    sum.linework = (float(words.w >> 16u) / 65535.0);
    return sum;
}
#endif

#endif
