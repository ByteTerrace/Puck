// source-scrgb.comp.hlsl — ImageSourceConversion.ScRgbImagePass: an imported half-float scRGB image, such as a desktop
// capture of an HDR display copied GPU-side into shared targets, converted to working values in a half-float RGBA image:
// one at SDR white, which the display encode shows at the paper-white level, with headroom above it.
//
// The decode is source-transfer's for scRGB content (ImageSourceConversion.ToWorking): a linear value of one is 80 cd/m²,
// read relative to the paper white and given the extended sRGB curve the display encode decodes, so nothing is clipped
// here and nothing is encoded twice. Alpha passes through unchanged.

// The generated interface declares the frame group and the pass group: the extent, the paper-white level, the image read
// at binding 1, the image written at binding 2, and the work counters each written pixel counts its texel into
// (puckCountWork). A document pass compiling the kernel declares no work counters, so it counts nothing.
#include "source-scrgb.interface.hlsli"
#include "image-source.hlsli"

// The color word of scRGB content: BT.709 primaries and the linear transfer function (ImageColorEncoding.Of(ScRgb)).
#define IMAGE_COLOR_SCRGB (IMAGE_TRANSFER_LINEAR << 16)

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if ((id.x >= passGroup.extent.x) || (id.y >= passGroup.extent.y)) {
        return;
    }

    float4 stored = source.Load(int3(id.xy, 0));

    image[id.xy] = float4(imageSourceToWorking(IMAGE_COLOR_SCRGB, stored.rgb, passGroup.paperWhiteNits), stored.a);
    puckCountWork(0u, 1u);
}
