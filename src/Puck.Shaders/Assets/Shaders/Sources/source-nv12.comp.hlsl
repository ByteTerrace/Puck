// source-nv12.comp.hlsl — ImageSourceConversion.Nv12Pass: an NV12 source to RGBA8.
//
// Plane 0 of the region is the Y rows and plane 1 the half-height rows of interleaved Cb, Cr pairs. Chroma is read at
// the co-sited sample with no filtering, converted under the header's matrix and range, and clamped by the RGBA8 store.

#include "image-source.hlsli"

[[vk::binding(0, 0)]] ByteAddressBuffer region : register(t0);
[[vk::binding(1, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> image : register(u1);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    ImageSourceHeader header = imageSourceHeader(region);

    if ((id.x >= header.width) || (id.y >= header.height)) {
        return;
    }

    uint luma = imageSourceByte(region, (header.plane0 + (id.y * header.stride0) + id.x));
    uint chroma = (header.plane1 + ((id.y / 2u) * header.stride1) + ((id.x / 2u) * 2u));
    float3 rgb = imageSourceYuvToRgb(luma, imageSourceByte(region, chroma), imageSourceByte(region, (chroma + 1u)), header.color);

    image[id.xy] = float4(saturate(rgb), 1.0);
}
