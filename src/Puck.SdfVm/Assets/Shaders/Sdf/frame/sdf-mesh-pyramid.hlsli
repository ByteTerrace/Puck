// The mesh depth pyramid the surface pass's occlusion reads (SdfWorldPackage.MeshPyramid), written by the pyramid pass
// (passes/sdf-mesh-pyramid.comp.hlsl): levels one to SdfMeshPyramidLevels of the mesh visibility target. A texel of level
// k stands for its 2^k block by one actual pixel of it, its representative, whose ray parameter and coordinate it holds,
// so a tap that reads it rebuilds that pixel's surface point exactly, as a level-zero tap does. A block's representative
// is the representative of one of its children, chosen on a rotated grid (sdfMeshPyramidChild) so the chosen pixels do not
// line up across blocks, and turned each frame with the occlusion's pattern step, so the frames a view averages read
// every child of a block in turn as a level-zero tap would. Each level lies in 8x8 tiles, Morton ordered within a tile, so the taps a warp sends into a coarse
// level share cache sectors. A texel is two words: the ray parameter's bits, zero where no mesh covers the pixel, then the
// pixel's x and y as 16-bit halves. A level the buffer cannot hold whole is absent (sdfMeshPyramidLevelsWithin).
#ifndef FRAME_SDF_MESH_PYRAMID_HLSLI
#define FRAME_SDF_MESH_PYRAMID_HLSLI

static const uint SdfMeshPyramidLevels = 4u;

// A level's extent: the view's extent halved per level, rounded up.
uint2 sdfMeshPyramidExtent(uint2 dims, uint level) {
    return ((dims + ((1u << level) - 1u)) >> level);
}
// The words one level takes: two a texel of its whole tiles.
uint sdfMeshPyramidLevelWords(uint2 extent) {
    uint2 tiles = ((extent + 7u) >> 3u);

    return ((tiles.x * tiles.y) * 128u);
}
// The levels a buffer of `words` words holds whole, from level one up.
uint sdfMeshPyramidLevelsWithin(uint2 dims, uint words) {
    uint used = 0u;
    uint levels = 0u;

    [unroll]
    for (uint level = 1u; (level <= SdfMeshPyramidLevels); level++) {
        used += sdfMeshPyramidLevelWords(sdfMeshPyramidExtent(dims, level));
        levels = ((used <= words) ? level : levels);
    }

    return levels;
}
// The child a block of `level` takes its representative from, inside the child level's extent, at a frame's pattern step.
uint2 sdfMeshPyramidChild(uint2 parent, uint2 childExtent, uint level, uint step) {
    uint turn = ((step + level) & 3u);

    return min(uint2(((parent.x << 1u) + (((parent.y & 1u) ^ 1u) ^ (turn & 1u))), ((parent.y << 1u) + (((parent.x & 1u) ^ 1u) ^ (turn >> 1u)))), (childExtent - 1u));
}
// The first word of a level's texel.
uint sdfMeshPyramidWord(uint2 dims, uint level, uint2 texel) {
    uint base = 0u;

    [loop]
    for (uint below = 1u; (below < level); below++) {
        base += sdfMeshPyramidLevelWords(sdfMeshPyramidExtent(dims, below));
    }

    uint2 extent = sdfMeshPyramidExtent(dims, level);
    uint tilesX = ((extent.x + 7u) >> 3u);
    uint2 local = (texel & 7u);
    uint morton = (((local.x & 1u) | ((local.x & 2u) << 1u) | ((local.x & 4u) << 2u)) |
        (((local.y & 1u) << 1u) | ((local.y & 2u) << 2u) | ((local.y & 4u) << 3u)));

    return (base + (((((texel.y >> 3u) * tilesX) + (texel.x >> 3u)) * 64u) + morton) * 2u);
}

#endif
