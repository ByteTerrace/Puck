// Every pixel is (gain, 1 - gain, 0.5), so a captured region's value is the gain's own arithmetic.
// The frame block is the pass's generated interface: the frame values and the pass's config.
#include "tint.interface.hlsli"

[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> image : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    image.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    image[id.xy] = float4(frameGroup.gain, (1.0 - frameGroup.gain), 0.5, 1.0);
}
