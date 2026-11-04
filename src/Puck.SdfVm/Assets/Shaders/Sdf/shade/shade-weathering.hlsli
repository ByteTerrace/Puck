// Generic coverage masks; every surface color and response comes from the material.
#ifndef SDF_SHADE_WEATHERING_HLSLI
#define SDF_SHADE_WEATHERING_HLSLI
float sdfLineCoverage(float2 uv, uint seed, float footprint) {
    float2 cell = floor(uv);
    float2 local = frac(uv) - 0.5;
    uint3 hash = sdfPcg3d(uint3(asuint(cell.x), asuint(cell.y), seed));
    float angle = (float)hash.x * SDF_INV_2POW32 * SDF_PI;
    float2 direction = float2(cos(angle), sin(angle));
    float distance = abs(dot(local, direction));
    float width = 0.02 + 0.06 * ((float)hash.y * SDF_INV_2POW32);
    return 1.0 - smoothstep(width, width + max(footprint, 0.001), distance);
}
void sdfRevealSurface(float3 color, float2 response, float coverage, inout SdfMaterialData material) {
    material.albedo = lerp(material.albedo, color, coverage);
    material.roughness = lerp(material.roughness, response.x, coverage);
    material.metal = lerp(material.metal, response.y, coverage);
    material.coat *= 1.0 - coverage;
}
void applyWeathering(float3 position, float3 normal, float upFacing, float curvature, float footprint,
    float4 lanes, inout SdfMaterialData material) {
    float4 w = material.weathering;
    float4 pattern = float4(material.underResponse[0].w, material.underResponse[1].w, material.depositResponse.zw);
    if (w.x + w.y + w.z + pattern.w <= 0.0) return;
    float4 controls = material.weatheringControls;
    float amount = max(saturate(lanes[(uint)controls.w]), controls.z);
    if (amount <= 0.0) return;
    uint seed = asuint(controls.x);
    float3 frequency = controls.y * pattern.xyz;
    float3 p = position * frequency;
    // A conservative world-space pixel support fades unresolved lattice variation toward its mean.
    // This is a bandwidth approximation, not an exact integration of the nonlinear reveal ramp.
    float rate = footprint * max(frequency.x, max(frequency.y, frequency.z));
    float noise = 0.5;
    if (w.x + w.z + pattern.w > 0.0) {
        // A thresholded single octave of the integer lattice reveals its cells: the reveal ramp cuts the trilinear
        // blend along lines parallel to the world axes, so ground deposits read as squares. The lattice is read
        // rotated off every axis (a fixed orthonormal turn) and with a second octave at about twice the frequency, each
        // octave fading by its own pixel support; the weights keep the single octave's spread, so a threshold reveals
        // about as much as before.
        static const float3x3 turn = float3x3(
            0.36, 0.48, -0.80,
            -0.80, 0.60, 0.00,
            0.48, 0.64, 0.60);
        float3 q = mul(turn, p);
        float coarse = sdfLatticeNoise3(q, seed) * (1.0 - smoothstep(0.35, 0.9, rate));
        float fine = sdfLatticeNoise3((q * 2.03) + 17.0, seed ^ 0x9E3779B9u) * (1.0 - smoothstep(0.35, 0.9, 2.03 * rate));
        noise = 0.5 + 0.5 * clamp((0.88 * coarse) + (0.47 * fine), -1.0, 1.0);
    }
    float edge = saturate(max(curvature, 0.0) * w.w) * noise;
    float lineMask = 0.0;
    if (w.y > 0.0) {
        float3 weights = pow(abs(normal), 4.0);
        weights /= max(dot(weights, float3(1.0, 1.0, 1.0)), 1.0e-6);
        lineMask = dot(weights, float3(
            sdfLineCoverage(p.yz, seed, footprint * max(frequency.y, frequency.z)),
            sdfLineCoverage(p.xz, seed + 1u, footprint * max(frequency.x, frequency.z)),
            sdfLineCoverage(p.xy, seed + 2u, footprint * max(frequency.x, frequency.y))));
    }
    float coverage = saturate(amount * max(max(w.x * edge, w.y * lineMask), pattern.w * noise));
    uint count = (uint)material.underResponse[0].z;
    // Unrolled to the stages' length and guarded, so every index is a constant: NVIDIA's Vulkan compiler keeps a
    // dynamically indexed local array in per-thread memory, and DXC's SPIR-V unroller keeps a loop with a break rolled.
    [unroll] for (uint i = 0u; i < 2u; i++) {
        if (i < count) {
            float4 stage = material.underColorThreshold[i];
            float aa = max(material.underResponse[1].z, max(min(rate, 0.25), 0.001));
            float reveal = smoothstep(max(stage.w - aa, 0.0), stage.w, coverage);
            sdfRevealSurface(stage.rgb, material.underResponse[i].xy, reveal, material);
        }
    }
    float settle = saturate(upFacing) * w.z * amount * noise * material.deposit.w;
    sdfRevealSurface(material.deposit.rgb, material.depositResponse.xy, settle, material);
}
#endif
