// Names an include that no file holds, below one that exists.
#include "present.hlsli"
#include "absent.hlsli"

[[vk::binding(0, 0)]] RWTexture2D<float4> image : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    image[id.xy] = Fill();
}
