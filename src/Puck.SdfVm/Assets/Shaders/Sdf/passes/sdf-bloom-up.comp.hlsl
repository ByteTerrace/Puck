#include "../isa/sdf-bloom.interface.hlsli"
#include "../../../../../Puck.Shaders/Assets/Shaders/Shared/bloom-filter.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent, sourceExtent;
    bloomOutput.GetDimensions(extent.x, extent.y);
    if (any(id.xy >= extent)) { return; }
    bloomSource.GetDimensions(sourceExtent.x, sourceExtent.y);
    float3 color = bloomTent(bloomSource, bloomSampler,
        bloomLinearAxis(id.x, sourceExtent.x, extent.x), bloomLinearAxis(id.y, sourceExtent.y, extent.y),
        sourceExtent, passGroup.bloomRadius);
    float3 fine = bloomFine.Load(int3(id.xy, 0)).rgb;
    bloomOutput[id.xy] = float4(lerp(fine, color, passGroup.bloomMix), passGroup.bloomScatter);
    puckCountWork(0u, 1u);
}
