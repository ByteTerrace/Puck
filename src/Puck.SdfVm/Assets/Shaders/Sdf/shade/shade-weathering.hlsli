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
    if (w.x + w.y + w.z <= 0.0) return;
    float4 controls = material.weatheringControls;
    float amount = max(saturate(lanes[(uint)controls.w]), controls.z);
    if (amount <= 0.0) return;
    uint seed = asuint(controls.x);
    float3 p = position * controls.y;
    float noise = 0.5 + 0.5 * sdfLatticeNoise3(p, seed);
    float edge = saturate(max(curvature, 0.0) * w.w) * noise;
    float3 weights = pow(abs(normal), 4.0);
    weights /= max(dot(weights, float3(1.0, 1.0, 1.0)), 1.0e-6);
    float lineMask = dot(weights, float3(
        sdfLineCoverage(p.yz, seed, footprint * controls.y),
        sdfLineCoverage(p.xz, seed + 1u, footprint * controls.y),
        sdfLineCoverage(p.xy, seed + 2u, footprint * controls.y)));
    float coverage = saturate(amount * max(w.x * edge, w.y * lineMask));
    uint count = (uint)material.underResponse[0].z;
    [loop] for (uint i = 0u; i < count; i++) {
        float4 stage = material.underColorThreshold[i];
        float aa = max(footprint * controls.y, 0.001);
        float reveal = smoothstep(max(stage.w - aa, 0.0), stage.w, coverage);
        sdfRevealSurface(stage.rgb, material.underResponse[i].xy, reveal, material);
    }
    float settle = saturate(upFacing) * w.z * amount * noise * material.deposit.w;
    sdfRevealSurface(material.deposit.rgb, material.depositResponse.xy, settle, material);
}
#endif
