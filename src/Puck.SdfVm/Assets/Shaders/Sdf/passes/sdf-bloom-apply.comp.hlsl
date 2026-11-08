#include "../isa/sdf-bloom.interface.hlsli"
#include "../../../../../Puck.Shaders/Assets/Shaders/Shared/bloom-filter.hlsli"
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent, sourceExtent;
    bloomOutput.GetDimensions(extent.x, extent.y);
    if (any(id.xy >= extent)) { return; }
    bloomSource.GetDimensions(sourceExtent.x, sourceExtent.y);
    float3 veil = bloomTent(bloomSource, bloomSampler,
        bloomLinearAxis(id.x, sourceExtent.x, extent.x), bloomLinearAxis(id.y, sourceExtent.y, extent.y), sourceExtent, 1.0);
    float4 direct = bloomFine.Load(int3(id.xy, 0));
    float3 selected = bloomSelected(direct.rgb, passGroup.bloomCutoff);
    bloomOutput[id.xy] = float4(passGroup.bloomMix < 0.0 ? veil : direct.rgb + passGroup.bloomScatter * (veil - selected), direct.a);
    puckCountWork(0u, 1u);
}
