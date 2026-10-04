// The noise kind: a fractal sum of the periodic 3D lattice (field/sdf-noise.hlsli) over the layer-frame direction scaled
// by Scale cells, its lattice sliding one period along the frame's up a cycle of the layer's clock, coloured from
// ColorLow to ColorHigh and covered where it rises past 1 − Coverage over Softness. At SDF_SKY_TIER_HIGH the sum takes
// Octaves octaves and below it at most two. Each octave hashes eight lattice corners, which count.
#ifndef SKY_KINDS_NOISE_HLSLI
#define SKY_KINDS_NOISE_HLSLI

float4 sdfSkyNoiseLayer(SdfSkyNoise noise, SdfSkyLayer layer, SdfSkySample sample) {
    if (noise.Coverage <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    sdfCountSky(layer.Detail, 0u, 0u, 1u, 0u, 0u);

    uint octaves = clamp(noise.Octaves, 1u, 8u);

    if (sample.tier < SDF_SKY_TIER_HIGH) {
        octaves = min(octaves, 2u);
    }

    float3 q = ((sample.local * noise.Scale) + float3(0.0, (layer.Phase * (float)SDF_NOISE_PERIOD_CELLS), 0.0));
    float value = sdfPeriodicFbm3(q, noise.Seed, octaves, noise.Gain);

    sdfCountSky(layer.Detail, 0u, 0u, 0u, (8u * octaves), 0u);

    float alpha = ((noise.Coverage >= 1.0) ? 1.0 : sdfSkyRise(((1.0 - noise.Coverage) + noise.Softness), noise.Softness, value));

    return float4(lerp(noise.ColorLow, noise.ColorHigh, value), alpha);
}
#endif
