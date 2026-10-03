// The aurora kind: curtains of rays rising from a wavering base. Around the layer frame's up, a direction's azimuth is a
// point on the unit circle, which the periodic lattice reads (so the curtain closes on itself with no seam): one fractal
// sum at Waves per turn wavers the base by Fold about Base, a second at Rays per turn brightens the rays. Above its base a
// curtain fades upward over Height, its colour moving from Color to TopColor; below it, nothing. Both sums slide one
// lattice period a cycle of the layer's clock, so the curtains move and the phase's wrap shows no seam. Below
// SDF_SKY_TIER_HIGH each sum takes one octave fewer, and at SDF_SKY_TIER_LOW the rays take one. Each octave hashes four
// lattice corners, which count. Its alpha is the curtain's brightness, so an add blend lights the sky beneath.
#ifndef SKY_KINDS_AURORA_HLSLI
#define SKY_KINDS_AURORA_HLSLI

float4 sdfSkyAuroraLayer(SdfSkyAurora aurora, SdfSkyLayer layer, SdfSkySample sample) {
    float3 direction = sample.local;

    if ((aurora.Intensity <= 0.0) || (aurora.Height <= 0.0) || (direction.y <= (aurora.Base - aurora.Fold - 0.05))) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    puckCountDetail(layer.Detail, 0u, 0u, 1u, 0u, 0u);

    float2 around = direction.xz;
    float horizontal = length(around);

    around = ((horizontal > 1.0e-6) ? (around / horizontal) : float2(1.0, 0.0));

    float drift = (layer.Phase * (float)SDF_NOISE_PERIOD_CELLS);
    uint waveOctaves = ((sample.tier >= SDF_SKY_TIER_HIGH) ? 3u : 2u);
    uint rayOctaves = ((sample.tier >= SDF_SKY_TIER_HIGH) ? 2u : ((sample.tier >= SDF_SKY_TIER_MEDIUM) ? 2u : 1u));
    float wave = sdfPeriodicFbm2(((around * (aurora.Waves * 0.15915494)) + float2(drift, 0.0)), aurora.Seed, waveOctaves);
    float rays = sdfPeriodicFbm2(((around * (aurora.Rays * 0.15915494)) + float2(0.0, drift)), (aurora.Seed ^ 0x85EBCA77u), rayOctaves);

    puckCountDetail(layer.Detail, 0u, 0u, 0u, (4u * (waveOctaves + rayOctaves)), 0u);

    float base = (aurora.Base + (aurora.Fold * ((2.0 * wave) - 1.0)));
    float rise = ((direction.y - base) / aurora.Height);

    if (rise <= -0.05) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float foot = smoothstep(-0.05, 0.0, rise);
    float fade = exp(-3.0 * max(rise, 0.0));
    float strength = saturate((((rays * rays) * 1.5) * foot) * fade);
    float3 color = (lerp(aurora.Color, aurora.TopColor, saturate(rise)) * aurora.Intensity);

    return float4(color, strength);
}
#endif
