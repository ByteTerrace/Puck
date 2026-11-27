// The glass's sky filter (SdfWorldPackage.GlassSkyFilter), one word a render pixel: how the glass a pixel shows tints the
// sky behind it. The lit image's coverage carries only the luminance of the share of the sky behind the glass that it
// passes, the RGB share's (`filter`) luminance, so the composite, which puts the sky in the rest, multiplies its sky share
// by the filter's chroma (sdfGlassSkyTint: the filter over its luminance) and shows the sky through the glass at that RGB
// share. The word holds the filter normalized to its largest channel, ten bits a channel from the low bit, and bit 30
// marking a written word; zero, which the glass clear leaves and a pixel with no glass keeps, passes the sky as it is.
// The glass pass writes it (sdf-glass-stage.hlsli) and the composite reads it (sdf-composite.comp.hlsl).
#ifndef FRAME_SDF_GLASS_SKY_FILTER_HLSLI
#define FRAME_SDF_GLASS_SKY_FILTER_HLSLI
#include "Shared/luma.hlsli"

static const uint SdfGlassSkyFilterWritten = (1u << 30u);

uint sdfGlassSkyFilterPack(float3 filter) {
    float most = max(max(filter.r, filter.g), filter.b);

    if (!(most > 1.0e-6)) {
        return 0u;
    }

    uint3 steps = (uint3)round(saturate(filter / most) * 1023.0);

    return (steps.r | (steps.g << 10u) | (steps.b << 20u) | SdfGlassSkyFilterWritten);
}
// The factor a sky share behind the glass is tinted by: the filter's chroma at unit luminance, or one for a word that
// holds none.
float3 sdfGlassSkyTint(uint word) {
    if ((word & SdfGlassSkyFilterWritten) == 0u) {
        return float3(1.0, 1.0, 1.0);
    }

    float3 normalized = (float3(float(word & 1023u), float((word >> 10u) & 1023u), float((word >> 20u) & 1023u)) / 1023.0);
    float luminance = dot(normalized, PuckRec709Luminance);

    return ((luminance > 1.0e-6) ? (normalized / luminance) : float3(1.0, 1.0, 1.0));
}
#endif
