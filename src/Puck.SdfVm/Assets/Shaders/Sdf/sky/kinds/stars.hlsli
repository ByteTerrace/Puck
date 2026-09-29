// Analytic stars in the caller's local sky frame. The generated interface declares SdfSkyStarsData.
#ifndef SKY_KIND_STARS_HLSLI
#define SKY_KIND_STARS_HLSLI
#include "../../field/sdf-hash.hlsli"
#include "../../field/sdf-octahedral.hlsli"

// RGB is straight emission, alpha is intrinsic coverage. The common layer wrapper applies opacity and blend once.
// Low omits twinkle entirely; the wrapper owns minimum-tier admission and masks.
float4 sdfSkyStars(float3 localDirection, SdfSkyStarsData parameters, uint quality, inout uint hashes) {
    if (parameters.Brightness <= 0.0 || parameters.Sparsity <= 0.0) return 0.0;

    float density = max(parameters.Density, 1.0);
    float2 cellF = ((sdfOctEncode(localDirection) * 0.5) + 0.5) * density;
    float2 cellId = floor(cellF);
    uint3 h = sdfPcg3d(uint3(asuint(cellId.x), asuint(cellId.y), parameters.Seed));
    hashes++;
    if ((float)h.x * SDF_INV_2POW32 > parameters.Sparsity) return 0.0;

    uint3 h2 = sdfPcg3d(h);
    hashes++;
    float luminosity = min(1.0, parameters.LuminosityFloor * pow(max((float)h2.x * SDF_INV_2POW32, 1e-6), -0.6666667));
    float spectrum = (float)h2.y * SDF_INV_2POW32 * 6.0;
    uint spectrumIndex = min((uint)spectrum, 5u);
    float3 colors[7] = {
        parameters.Spectrum0, parameters.Spectrum1, parameters.Spectrum2, parameters.Spectrum3,
        parameters.Spectrum4, parameters.Spectrum5, parameters.Spectrum6
    };
    float3 tint = lerp(colors[spectrumIndex], colors[spectrumIndex + 1u], spectrum - (float)spectrumIndex);

    if (quality > 0u && parameters.TwinkleDepth > 0.0 && (float)h2.z * SDF_INV_2POW32 < parameters.TwinkleShare) {
        // Integer-selected harmonics close at the unit phase boundary; the shared resolver owns elapsed time.
        uint3 h3 = sdfPcg3d(h2);
        hashes++;
        float harmonicA = (float)(1u + h3.x % 3u);
        float harmonicB = (float)(2u + h3.y % 3u);
        float offset = (float)h3.z * SDF_INV_2POW32;
        float flicker = 0.5 + 0.5 * (
            sin(6.28318531 * (harmonicA * parameters.TwinklePhase + offset)) *
            sin(6.28318531 * (harmonicB * parameters.TwinklePhase + offset * 1.7)));
        luminosity *= 1.0 - parameters.TwinkleDepth * flicker;
    }
    float2 starUv = lerp(parameters.Inset.xx, (1.0 - parameters.Inset).xx,
        float2((float)h.y * SDF_INV_2POW32, (float)h.z * SDF_INV_2POW32));
    float3 starDirection = sdfOctDecode(((cellId + starUv) / density) * 2.0 - 1.0);
    float radius = (parameters.RadiusFraction * 3.14159265 / density) *
        lerp(parameters.RadiusFloor, 1.0, sqrt(luminosity));
    float separation = 1.0 - dot(localDirection, starDirection);
    float coverage = smoothstep(0.5 * radius * radius, 0.0, separation);
    return float4(parameters.Brightness * luminosity * tint, coverage);
}
#endif
