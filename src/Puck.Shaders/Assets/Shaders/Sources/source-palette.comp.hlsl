// source-palette.comp.hlsl — ImageSourceConversion.PalettePass: a palette-indexed source to RGBA8.
//
// Plane 0 of the region is the 256-entry RGBA8 palette and plane 1 the index rows, one byte a pixel. One thread a pixel;
// a pixel outside the header's extent writes nothing.

#include "image-source.hlsli"

[[vk::binding(1, 3)]] ByteAddressBuffer region : register(t1, space3);
[[vk::binding(2, 3)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> image : register(u2, space3);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    ImageSourceHeader header = imageSourceHeader(region);

    if ((id.x >= header.width) || (id.y >= header.height)) {
        return;
    }

    uint index = imageSourceByte(region, (header.plane1 + (id.y * header.stride1) + id.x));

    image[id.xy] = imageSourceUnpackRgba8(region.Load(header.plane0 + (index * 4u)));
}
