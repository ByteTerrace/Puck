// Includes a file beside the pipeline's directory rather than below it, so it lies outside a closure rooted at the
// pipeline and inside one rooted a directory higher.
#include "../shared/outside.hlsli"

[[vk::binding(0, 0)]] RWTexture2D<float4> image : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    image[id.xy] = Outside();
}
