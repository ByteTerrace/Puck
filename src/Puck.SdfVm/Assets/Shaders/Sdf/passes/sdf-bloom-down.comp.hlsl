#include "../isa/sdf-bloom.interface.hlsli"

float3 bloomTap(float2 uv, float2 delta, float2 texel) {
    float3 light = max(bloomSource.SampleLevel(bloomSampler, clamp(uv + delta * texel, texel * 0.5, 1.0 - texel * 0.5), 0).rgb, 0.0);
    if (passGroup.bloomCutoff > 0.0) {
        float luminance = dot(light, float3(0.2126, 0.7152, 0.0722));
        light *= smoothstep(passGroup.bloomCutoff * 0.5, passGroup.bloomCutoff * 1.5, luminance);
    }
    return light;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent, sourceExtent;
    bloomOutput.GetDimensions(extent.x, extent.y);
    if (any(id.xy >= extent)) { return; }
    bloomSource.GetDimensions(sourceExtent.x, sourceExtent.y);
    float2 uv = (float2(id.xy) + 0.5) / float2(extent);
    float2 texel = 1.0 / float2(sourceExtent);
    float3 color = bloomTap(uv, float2(0, 0), texel) * 0.125;
    color += (bloomTap(uv, float2(-2,-2), texel) + bloomTap(uv, float2(2,-2), texel)
        + bloomTap(uv, float2(-2,2), texel) + bloomTap(uv, float2(2,2), texel)) * 0.03125;
    color += (bloomTap(uv, float2(-2,0), texel) + bloomTap(uv, float2(2,0), texel)
        + bloomTap(uv, float2(0,-2), texel) + bloomTap(uv, float2(0,2), texel)) * 0.0625;
    color += (bloomTap(uv, float2(-1,-1), texel) + bloomTap(uv, float2(1,-1), texel)
        + bloomTap(uv, float2(-1,1), texel) + bloomTap(uv, float2(1,1), texel)) * 0.125;
    bloomOutput[id.xy] = float4(color, passGroup.bloomScatter);
    puckCountWork(0u, 1u);
}
