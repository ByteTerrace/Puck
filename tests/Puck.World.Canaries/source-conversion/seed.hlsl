// The producer stand-in: writes a 32x32 palette-indexed region and a 32x32 NV12 region in the uploaded-source layout
// (ImageSourceUploadLayout), which the shipped conversion passes then read. The pass runs over the frame, at least 16x16,
// and thread t = 16y + x, for x below 16, writes word t of each plane it covers. SourceConversionCanaryFixtureTests
// rebuilds both regions in C# from the same rules and derives every expected color through the CPU reference.
//
// Palette region: header, then palette entry i = (i, 255 - i, 3i mod 256, 255) as RGBA8, then the index rows, where a
// pixel's index is 5 + 64 (x / 16) + 128 (y / 16). NV12 region: header naming BT.709 limited range, then Y and CbCr by
// quadrant: top left (235, 128, 128), top right (63, 102, 240), bottom left (126, 128, 168), bottom right (81, 90, 110).
#ifndef SEED_NV12_MATRIX
#define SEED_NV12_MATRIX 1u
#endif
#ifndef SEED_INDEX_BASE
#define SEED_INDEX_BASE 5u
#endif

[[vk::binding(0, 0)]] RWByteAddressBuffer paletteRegion : register(u0);
[[vk::binding(1, 0)]] RWByteAddressBuffer nv12Region : register(u1);

uint quadrant(uint x, uint y) {
    return ((x >= 16u) ? 1u : 0u) + ((y >= 16u) ? 2u : 0u);
}

uint4 yuv(uint q) {
    if (q == 0u) {
        return uint4(235u, 128u, 128u, 0u);
    }
    if (q == 1u) {
        return uint4(63u, 102u, 240u, 0u);
    }
    if (q == 2u) {
        return uint4(126u, 128u, 168u, 0u);
    }
    return uint4(81u, 90u, 110u, 0u);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (id.x >= 16u) {
        return;
    }

    uint t = ((id.y * 16u) + id.x);

    if (t == 0u) {
        // Width, height, format (Indexed8 = 3), color (BT.709 matrix, limited, sRGB), palette at 32 (1024 bytes),
        // indices at 1056 with a 32-byte stride.
        paletteRegion.Store4(0, uint4(32u, 32u, 3u, 1u));
        paletteRegion.Store4(16, uint4(32u, 1024u, 1056u, 32u));
        // Width, height, format (Nv12 = 4), color, Y at 32 with a 32-byte stride, CbCr at 1056 with a 32-byte stride.
        nv12Region.Store4(0, uint4(32u, 32u, 4u, SEED_NV12_MATRIX));
        nv12Region.Store4(16, uint4(32u, 32u, 1056u, 32u));
    }

    if (t < 256u) {
        paletteRegion.Store((32u + (t * 4u)), (t | ((255u - t) << 8) | (((t * 3u) & 0xFFu) << 16) | 0xFF000000u));

        // Index word t holds pixels 4t to 4t + 3 of one row; four pixels never straddle a quadrant.
        uint x = ((t * 4u) % 32u);
        uint y = ((t * 4u) / 32u);
        uint index = (SEED_INDEX_BASE + (64u * (x / 16u)) + (128u * (y / 16u)));

        paletteRegion.Store((1056u + (t * 4u)), (index * 0x01010101u));

        uint luma = yuv(quadrant(x, y)).x;

        nv12Region.Store((32u + (t * 4u)), (luma * 0x01010101u));
    }

    if (t < 128u) {
        // CbCr word t holds two chroma pairs of chroma row (4t) / 32, covering luma columns 2c and 2c + 2.
        uint chromaColumn = (((t * 4u) % 32u) / 2u);
        uint chromaRow = ((t * 4u) / 32u);
        uint4 sample = yuv(quadrant((chromaColumn * 2u), (chromaRow * 2u)));

        nv12Region.Store((1056u + (t * 4u)), (sample.y | (sample.z << 8) | (sample.y << 16) | (sample.z << 24)));
    }
}
