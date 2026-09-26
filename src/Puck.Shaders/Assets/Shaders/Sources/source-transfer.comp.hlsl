// source-transfer.comp.hlsl — ImageSourceConversion.TransferPass: an RGBA8 or R10G10B10A2 source decoded to linear
// light in a half-float RGBA image, 1 being the SDR reference white.
//
// The header's transfer function decides the decode: sRGB, linear, or the perceptual quantizer divided by the reference
// white's luminance. Alpha passes through unchanged.

// The generated interface declares the frame group and the pass group: the extent, the region read at binding 1 and the
// image written at binding 2.
#include "source-transfer.interface.hlsli"
#include "image-source.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    ImageSourceHeader header = imageSourceHeader(region);

    if ((id.x >= header.width) || (id.y >= header.height)) {
        return;
    }

    uint word = region.Load(header.plane0 + (id.y * header.stride0) + (id.x * 4u));
    uint transfer = ((header.color >> 16) & 0xFFu);
    float4 encoded = ((header.format == IMAGE_FORMAT_R10G10B10A2)
        ? (float4(word & 0x3FFu, (word >> 10) & 0x3FFu, (word >> 20) & 0x3FFu, 0.0) / 1023.0) + float4(0.0, 0.0, 0.0, (float(word >> 30) / 3.0))
        : imageSourceUnpackRgba8(word));

    image[id.xy] = float4(imageSourceDecode(transfer, encoded.r), imageSourceDecode(transfer, encoded.g), imageSourceDecode(transfer, encoded.b), encoded.a);
}
