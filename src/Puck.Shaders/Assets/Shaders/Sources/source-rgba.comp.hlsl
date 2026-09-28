// source-rgba.comp.hlsl — ImageSourceConversion.RgbaPass: an RGBA8 or BGRA8 source to RGBA8.
//
// Plane 0 of the region is the pixel rows, four bytes a pixel; a BGRA8 source's red and blue swap on the way through.

// The generated interface declares the frame group and the pass group: the extent, the region read at binding 1, the
// image written at binding 2, and the work counters each written pixel counts its texel into (puckCountWork). A document
// pass compiling the kernel declares no work counters, so it counts nothing.
#include "source-rgba.interface.hlsli"
#include "image-source.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    ImageSourceHeader header = imageSourceHeader(region);

    if ((id.x >= header.width) || (id.y >= header.height)) {
        return;
    }

    float4 pixel = imageSourceUnpackRgba8(region.Load(header.plane0 + (id.y * header.stride0) + (id.x * 4u)));

    image[id.xy] = ((header.format == IMAGE_FORMAT_B8G8R8A8) ? pixel.bgra : pixel);
    puckCountWork(0u, 1u);
}
