// The stars kind: a per-cell PCG3D hash (seed folded in) over the octahedral sky projection of the layer-frame direction
// picks Sparsity of the cells to carry a star; two hash channels place the star inside its cell (kept Inset from the
// walls so a disc never straddles a cell it is not tested in). A second hash of the first, paid only by the cells that
// carry a star, gives each its own luminosity and colour: luminosity follows the count law of sources spread uniformly
// through space (N(>F) ∝ F^-3/2, so F = floor·u^-2/3, capped at the peak), the disc growing mildly with it; colour is a
// blackbody tint picked log-uniformly in temperature from about 3000 K to about 15000 K, each tint normalized to a unit
// peak channel. A hash-chosen TwinkleShare of the stars dip by TwinkleDepth and recover, each on its own small harmonic
// and phase of the host-integrated TwinklePhase; at SDF_SKY_TIER_LOW no star twinkles. The disc is
// measured angularly against RadiusFraction of one cell's angular pitch (about π/density), so a star is round everywhere.
// Drawn only above the layer frame's horizon and only with brightness; its colour is the star's light and its alpha the
// star's coverage, so an add blend lights the sky beneath and an over blend paints the star over it. Each hash counts.
#ifndef SKY_KINDS_STARS_HLSLI
#define SKY_KINDS_STARS_HLSLI

static const float3 SdfStarSpectrum[7] = {      // blackbody tints, unit peak channel: 3000, 4000, 5000, 6500, 8000, 10000, 15000 K
    float3(1.00, 0.71, 0.42),
    float3(1.00, 0.82, 0.64),
    float3(1.00, 0.89, 0.81),
    float3(1.00, 0.98, 0.99),
    float3(0.89, 0.91, 1.00),
    float3(0.79, 0.85, 1.00),
    float3(0.71, 0.80, 1.00)
};

float4 sdfSkyStarsLayer(SdfSkyStars stars, SdfSkyLayer layer, SdfSkySample sample) {
    float3 direction = sample.local;

    if ((direction.y <= 0.0) || (stars.Brightness <= 0.0)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    puckCountDetail(layer.Detail, 0u, 0u, 1u, 0u, 0u);

    float density = max(stars.Density, 1.0);
    float2 cellF = (((sdfOctEncode(direction) * 0.5) + 0.5) * density);
    float2 cellId = floor(cellF);
    uint3 h = sdfPcg3d(uint3(asuint(cellId.x), asuint(cellId.y), stars.Seed));
    puckCountDetail(layer.Detail, 0u, 0u, 0u, 1u, 0u);
    float existence = ((float)h.x * SDF_INV_2POW32);

    if (existence > stars.Sparsity) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    uint3 h2 = sdfPcg3d(h);
    puckCountDetail(layer.Detail, 0u, 0u, 0u, 1u, 0u);
    float luminosity = min(1.0, (stars.LuminosityFloor * pow(max(((float)h2.x * SDF_INV_2POW32), 1e-6), -0.6666667)));
    float spectrum = (((float)h2.y * SDF_INV_2POW32) * 6.0);
    uint spectrumIndex = min((uint)spectrum, 5u);
    float3 tint = lerp(SdfStarSpectrum[spectrumIndex], SdfStarSpectrum[(spectrumIndex + 1u)], (spectrum - (float)spectrumIndex));

    if ((sample.tier > SDF_SKY_TIER_LOW) && (((float)h2.z * SDF_INV_2POW32) < stars.TwinkleShare)) {
        // Two sines at distinct small harmonics of the period, phase-offset per star, multiplied: an irregular dip
        // pattern that still closes exactly at the period boundary, so the phase's wrap never shows a seam.
        uint3 h3 = sdfPcg3d(h2);
        puckCountDetail(layer.Detail, 0u, 0u, 0u, 1u, 0u);
        float phase = stars.TwinklePhase;
        float harmonicA = (float)(1u + (h3.x % 3u));
        float harmonicB = (float)(2u + (h3.y % 3u));
        float offset = ((float)h3.z * SDF_INV_2POW32);
        float flicker = (0.5 + (0.5 * (sin(6.28318531 * ((harmonicA * phase) + offset)) * sin(6.28318531 * ((harmonicB * phase) + (offset * 1.7))))));

        luminosity *= (1.0 - (stars.TwinkleDepth * flicker));
    }

    float2 starUv = lerp(stars.Inset.xx, (1.0 - stars.Inset).xx, float2(((float)h.y * SDF_INV_2POW32), ((float)h.z * SDF_INV_2POW32)));
    float3 starDirection = sdfOctDecode((((cellId + starUv) / density) * 2.0) - 1.0);
    // 1 - cos(angle) ≈ angle²/2 for the small angles a star subtends: compare against the radius squared over two, no acos.
    float radius = (((stars.RadiusFraction * 3.14159265) / density) * lerp(0.6, 1.0, sqrt(luminosity)));
    float separation = (1.0 - dot(direction, starDirection));
    float coverage = smoothstep((0.5 * (radius * radius)), 0.0, separation);

    return float4(((stars.Brightness * luminosity) * tint), coverage);
}
#endif
