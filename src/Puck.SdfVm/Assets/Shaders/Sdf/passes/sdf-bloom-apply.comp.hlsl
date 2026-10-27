#include "../isa/sdf-bloom.interface.hlsli"
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent, sourceExtent;
    bloomOutput.GetDimensions(extent.x, extent.y);
    if (any(id.xy >= extent)) { return; }
    bloomSource.GetDimensions(sourceExtent.x, sourceExtent.y);
    float2 uv = (float2(id.xy) + 0.5) / float2(extent);
    float2 texel = 1.0 / float2(sourceExtent);
    float3 veil = 0.0;
    [unroll] for (int y = -1; y <= 1; y++) {
        [unroll] for (int x = -1; x <= 1; x++) {
            veil += float((2 - abs(x)) * (2 - abs(y))) / 16.0 * bloomSource.SampleLevel(bloomSampler,
                clamp(uv + float2(x,y) * texel, texel * 0.5, 1.0 - texel * 0.5), 0).rgb;
        }
    }
    float4 direct = bloomFine.Load(int3(id.xy, 0));
    float3 selected = direct.rgb;
    if (passGroup.bloomCutoff > 0.0) {
        selected *= smoothstep(passGroup.bloomCutoff * 0.5, passGroup.bloomCutoff * 1.5,
            dot(direct.rgb, float3(0.2126, 0.7152, 0.0722)));
    }
    bloomOutput[id.xy] = float4(passGroup.bloomMix < 0.0 ? veil : direct.rgb + passGroup.bloomScatter * (veil - selected), direct.a);
    puckCountWork(0u, 1u);
}
