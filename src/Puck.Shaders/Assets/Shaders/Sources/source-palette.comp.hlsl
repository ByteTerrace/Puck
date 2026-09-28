// source-palette.comp.hlsl — ImageSourceConversion.PalettePass: a palette-indexed source to RGBA8.
//
// Plane 0 of the region is the 256-entry RGBA8 palette and plane 1 the index rows, one byte a pixel. One thread a pixel;
// a pixel outside the header's extent writes nothing.

// The generated interface declares the frame group and the pass group: the extent, the region read at binding 1, the
// image written at binding 2, and the work counters each written pixel counts its texel into (puckCountWork). A document
// pass compiling the kernel declares no work counters, so it counts nothing.
#include "source-palette.interface.hlsli"
#include "image-source.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    ImageSourceHeader header = imageSourceHeader(region);

    if ((id.x >= header.width) || (id.y >= header.height)) {
        return;
    }

    uint index = imageSourceByte(region, (header.plane1 + (id.y * header.stride1) + id.x));

    image[id.xy] = imageSourceUnpackRgba8(region.Load(header.plane0 + (index * 4u)));
#if defined(PUCK_WORK_COUNTERS)
    puckCountWork(0u, 1u);
#endif
}
