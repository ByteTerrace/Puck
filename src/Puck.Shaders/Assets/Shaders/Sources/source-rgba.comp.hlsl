// source-rgba.comp.hlsl — ImageSourceConversion.RgbaPass: an RGBA8 or BGRA8 source to RGBA8.
//
// Plane 0 of the region is the pixel rows, four bytes a pixel; a BGRA8 source's red and blue swap on the way through.

#include "image-source.hlsli"

[[vk::binding(0, 0)]] ByteAddressBuffer region : register(t0);
[[vk::binding(1, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> image : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    ImageSourceHeader header = imageSourceHeader(region);

    if ((id.x >= header.width) || (id.y >= header.height)) {
        return;
    }

    float4 pixel = imageSourceUnpackRgba8(region.Load(header.plane0 + (id.y * header.stride0) + (id.x * 4u)));

    image[id.xy] = ((header.format == IMAGE_FORMAT_B8G8R8A8) ? pixel.bgra : pixel);
}
