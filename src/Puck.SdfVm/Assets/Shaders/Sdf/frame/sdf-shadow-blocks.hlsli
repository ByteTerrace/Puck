// A shadow layer pair's block bounds (SdfWorldPackage.MeshShadowNearBlocks): what the reduction
// (passes/sdf-shadow-blocks.comp.hlsl) writes after each drawing into the pair and the surface pass classifies a receiver
// by before it filters (sdfMeshShadowClass in passes/sdf-surface-stage.hlsli). This module owns the record's layout.
//
// A layer is cut into blocks of SDF_SHADOW_BLOCK_EDGE texels a side, in row order. A block holds two records, the depth
// layer's and then the exit layer's, each SDF_SHADOW_BLOCK_LAYER_WORDS words:
// 0 to 2: the layer's plane over the block, in the layer's stored depth (one at the cascade's near plane, zero at its
//     far): the depth at the block's centre, then its change per texel along x and along y. Any plane serves; the
//     reduction fits the block's own texels by least squares, so a slope the light meets at an angle fits closely.
// 3 and 4: the least and the greatest stored depth less the plane, over every texel of the layer within
//     SDF_SHADOW_BLOCK_APRON texels of the block: the block's own and an apron about it. A receiver's blocker search and
//     filter read texels within SDF_MESH_SHADOW_FILTER_TEXELS of its own, so the record of the one block that holds its
//     texel bounds every texel it can read. Each is widened by SdfShadowBlockSlack against the rounding between the
//     reduction's arithmetic and a reader's.
// 5: flags. SdfShadowBlockReduced is set once the record holds what the layers hold: a record a clear left zero says
//     nothing, and a reader that finds it filters. SdfShadowBlockGlass is set where the entry's glass extinction marks a
//     tile within the same reach (sdf-shadow-optical.hlsli's tile bitmap), since an unblocked tap under glass reads the
//     extinction and the bounds say nothing of it.
#ifndef FRAME_SDF_SHADOW_BLOCKS_HLSLI
#define FRAME_SDF_SHADOW_BLOCKS_HLSLI
#include "../isa/sdf-render.hlsli"

#if SDF_SHADOW_BLOCK_LAYER_WORDS != 6
#error A shadow block's layer record holds a plane, two bounds and a flags word.
#endif

static const uint SdfShadowBlockLowWord = 3u;
static const uint SdfShadowBlockHighWord = 4u;
static const uint SdfShadowBlockFlagsWord = 5u;
static const uint SdfShadowBlockReduced = 1u;
static const uint SdfShadowBlockGlass = 2u;
// The stored depth each bound is widened by: a few units in the last place of a depth near one, past the rounding of the
// plane's evaluation on either side.
static const float SdfShadowBlockSlack = 4.0e-6;

// The blocks along each side of a layer `side` texels wide.
uint sdfShadowBlocksAcross(uint side) {
    return ((side + (SDF_SHADOW_BLOCK_EDGE - 1u)) / SDF_SHADOW_BLOCK_EDGE);
}
// The first word of a block's record of one layer: the depth layer's, or the exit layer's after it.
uint sdfShadowBlockWord(uint side, uint2 block, bool exit) {
    return ((((block.y * sdfShadowBlocksAcross(side)) + block.x) * (2u * SDF_SHADOW_BLOCK_LAYER_WORDS)) + (exit ? SDF_SHADOW_BLOCK_LAYER_WORDS : 0u));
}

#endif
