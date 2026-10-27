#include "../isa/sdf-bloom.interface.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent, sourceExtent;
    bloomOutput.GetDimensions(extent.x, extent.y);
    if (any(id.xy >= extent)) { return; }
    bloomSource.GetDimensions(sourceExtent.x, sourceExtent.y);
    float2 uv = (float2(id.xy) + 0.5) / float2(extent);
    float2 texel = 1.0 / float2(sourceExtent);
    float3 color = 0.0;
    [unroll] for (int y = -1; y <= 1; y++) {
        [unroll] for (int x = -1; x <= 1; x++) {
            float weight = float((2 - abs(x)) * (2 - abs(y))) / 16.0;
            color += weight * bloomSource.SampleLevel(bloomSampler,
                clamp(uv + float2(x,y) * texel * passGroup.bloomRadius, texel * 0.5, 1.0 - texel * 0.5), 0).rgb;
        }
    }
    float3 fine = bloomFine.Load(int3(id.xy, 0)).rgb;
    bloomOutput[id.xy] = float4(lerp(fine, color, passGroup.bloomMix), passGroup.bloomScatter);
    puckCountWork(0u, 1u);
}
