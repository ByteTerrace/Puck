// source-transfer.comp.hlsl — ImageSourceConversion.TransferPass: an RGBA8, R10G10B10A2 or half-float RGBA source
// converted to working values in a half-float RGBA image: one at SDR white, which the display encode shows at the
// paper-white level, with headroom above it.
//
// The header's transfer function decides the decode to linear light relative to the paper white: sRGB, scRGB's linear
// scale, or the perceptual quantizer's luminance over the paper white. BT.2020 primaries move to BT.709, and the result
// takes the extended sRGB curve the display encode decodes, so nothing is clipped here and nothing is encoded twice.
// Alpha passes through unchanged.

// The generated interface declares the frame group and the pass group: the extent, the paper-white level, the region read
// at binding 1, the image written at binding 2, and the work counters each written pixel counts its texel into
// (puckCountWork). A document pass compiling the kernel declares no work counters, so it counts nothing.
#include "source-transfer.interface.hlsli"
#include "image-source.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    ImageSourceHeader header = imageSourceHeader(region);

    if ((id.x >= header.width) || (id.y >= header.height)) {
        return;
    }

    uint row = (header.plane0 + (id.y * header.stride0));
    float4 stored;

    if (header.format == IMAGE_FORMAT_R16G16B16A16F) {
        uint2 words = region.Load2(row + (id.x * 8u));

        stored = float4(f16tof32(words.x), f16tof32(words.x >> 16), f16tof32(words.y), f16tof32(words.y >> 16));
    } else {
        uint word = region.Load(row + (id.x * 4u));

        stored = ((header.format == IMAGE_FORMAT_R10G10B10A2)
            ? (float4(word & 0x3FFu, (word >> 10) & 0x3FFu, (word >> 20) & 0x3FFu, 0.0) / 1023.0) + float4(0.0, 0.0, 0.0, (float(word >> 30) / 3.0))
            : imageSourceUnpackRgba8(word));
    }

    image[id.xy] = float4(imageSourceToWorking(header.color, stored.rgb, passGroup.paperWhiteNits), stored.a);
    puckCountWork(0u, 1u);
}
