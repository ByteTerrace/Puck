// The shadow block reduction (SdfKernel.ShadowBlocks): one group a block of a shadow layer pair, writing the block's
// records (frame/sdf-shadow-blocks.hlsli owns the layout): for the depth layer and then the exit layer, the plane fitted
// to the block's own texels and the least and greatest depth off it over the block and its apron, and whether glass
// extinction lies within that reach. The residency's shadow upload dispatches it over the blocks a drawing touched, after
// the drawing and before any view's submission (SdfWorldShadows), so the surface pass reads bounds of the layers it
// samples.
//
// The pushed index names the dispatch's first block, its column in bits 0 to 7 and its row in bits 8 to 15, and the
// group's id is its block's offset from it. With bit SDF_SHADOW_BLOCK_FLAGS_ONLY_SHIFT set the dispatch rewrites the
// glass flag alone, for an extinction drawn again under layers that stand.
//
// A bound holds whatever plane it is taken against, so the plane's fit is no part of its truth: a texel past the layer's
// edge joins the fit as the edge texel it clamps to, and joins no bound, since a receiver whose reach leaves the layer
// is never classified.
#include "../isa/sdf-shadow-blocks.interface.hlsli"
#include "../frame/sdf-shadow-blocks.hlsli"
#include "../frame/sdf-shadow-optical.hlsli"

// A block's area: its own texels and the apron about them.
#define SDF_BLOCK_AREA_EDGE (SDF_SHADOW_BLOCK_EDGE + (2u * SDF_SHADOW_BLOCK_APRON))
#define SDF_BLOCK_AREA (SDF_BLOCK_AREA_EDGE * SDF_BLOCK_AREA_EDGE)
#define SDF_BLOCK_LANES 64u
// The extinction's tiles an area can meet along one axis: those its edge spans, and one more for where it starts in a
// tile.
#define SDF_BLOCK_TILES (((SDF_BLOCK_AREA_EDGE + (SDF_SHADOW_OPTICAL_TILE_EDGE - 1u)) / SDF_SHADOW_OPTICAL_TILE_EDGE) + 1u)
#if (SDF_BLOCK_TILES * SDF_BLOCK_TILES) > SDF_BLOCK_LANES
#error Each lane of a group tests one glass tile of the block's area.
#endif

// The area's stored depths, in row order from its first texel; negative for a texel past the layer's edge.
groupshared float sdfBlockDepths[SDF_BLOCK_AREA];
// Each lane's share of the fit's sums over the block's own texels: the depth, and the depth times the texel's offset from
// the block's centre along x and along y.
groupshared float sdfBlockSums[SDF_BLOCK_LANES * 3u];
groupshared float sdfBlockPlane[3];
// Each lane's least and greatest depth off the plane.
groupshared float sdfBlockLow[SDF_BLOCK_LANES];
groupshared float sdfBlockHigh[SDF_BLOCK_LANES];
groupshared uint sdfBlockGlass;

// An area entry's texel centre as an offset from the block's centre, in texels.
float2 sdfBlockOffset(uint entry) {
    return ((float2(float(entry % SDF_BLOCK_AREA_EDGE), float(entry / SDF_BLOCK_AREA_EDGE)) - float(SDF_SHADOW_BLOCK_APRON)) - (0.5 * float(SDF_SHADOW_BLOCK_EDGE - 1u)));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex) {
    uint side;
    uint height;
    uint words;
    uint stride;

    shadowBlocksDepth.GetDimensions(side, height);
    shadowBlocksRW.GetDimensions(words, stride);

    uint2 block = (uint2((pushedIndex.index & 0xFFu), ((pushedIndex.index >> 8u) & 0xFFu)) + groupId.xy);
    bool flagsOnly = (((pushedIndex.index >> SDF_SHADOW_BLOCK_FLAGS_ONLY_SHIFT) & 1u) != 0u);

    // Uniform across the group: every lane of it shares the block.
    if (any(block >= sdfShadowBlocksAcross(side)) || ((sdfShadowBlockWord(side, block, true) + SDF_SHADOW_BLOCK_LAYER_WORDS) > words)) {
        return;
    }

    int2 origin = ((int2(block) * (int)SDF_SHADOW_BLOCK_EDGE) - (int)SDF_SHADOW_BLOCK_APRON);

    if (lane == 0u) {
        sdfBlockGlass = 0u;
    }
    GroupMemoryBarrierWithGroupSync();

    // A tile a lane, over the tiles the area's texels inside the layer meet. A buffer that ends before the tile bitmap
    // (the filler an entry with no extinction binds) marks none.
    uint opticalWords;

    shadowBlocksOptical.GetDimensions(opticalWords, stride);

    uint bitmap = sdfShadowOpticalBitmap(side);

    if ((opticalWords > bitmap) && (lane < (SDF_BLOCK_TILES * SDF_BLOCK_TILES))) {
        uint2 firstTile = (uint2(max(origin, int2(0, 0))) / SDF_SHADOW_OPTICAL_TILE_EDGE);
        uint2 lastTile = (uint2(min((origin + (int)SDF_BLOCK_AREA_EDGE), int2((int)side, (int)side)) - 1) / SDF_SHADOW_OPTICAL_TILE_EDGE);
        uint2 tile = (firstTile + uint2((lane % SDF_BLOCK_TILES), (lane / SDF_BLOCK_TILES)));

        if (all(tile <= lastTile)) {
            uint index = ((tile.y * sdfShadowOpticalTilesAcross(side)) + tile.x);
            uint word = (bitmap + (index / 32u));

            if ((word < opticalWords) && (((shadowBlocksOptical[word] >> (index % 32u)) & 1u) != 0u)) {
                InterlockedOr(sdfBlockGlass, 1u);
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    uint glass = ((sdfBlockGlass != 0u) ? SdfShadowBlockGlass : 0u);

    // The pushed index is the dispatch's, so every lane takes this branch or none does.
    if (flagsOnly) {
        if (lane < 2u) {
            uint word = (sdfShadowBlockWord(side, block, (lane == 1u)) + SdfShadowBlockFlagsWord);

            shadowBlocksRW[word] = ((shadowBlocksRW[word] & ~SdfShadowBlockGlass) | glass);
        }
        return;
    }

    [unroll] for (uint layer = 0u; (layer < 2u); layer++) {
        float3 sum = float3(0.0, 0.0, 0.0);
        uint entry;

        [loop] for (entry = lane; (entry < SDF_BLOCK_AREA); entry += SDF_BLOCK_LANES) {
            int2 at = int2((int)(entry % SDF_BLOCK_AREA_EDGE), (int)(entry / SDF_BLOCK_AREA_EDGE));
            int2 texel = (origin + at);
            int3 clamped = int3(clamp(texel, int2(0, 0), (int2((int)side, (int)side) - 1)), 0);
            float depth = ((layer == 0u) ? shadowBlocksDepth.Load(clamped).x : shadowBlocksExit.Load(clamped).x);
            int2 own = (at - (int)SDF_SHADOW_BLOCK_APRON);

            sdfBlockDepths[entry] = ((all(texel >= 0) && all(texel < (int)side)) ? depth : -1.0);
            if (all(own >= 0) && all(own < (int)SDF_SHADOW_BLOCK_EDGE)) {
                float2 offset = sdfBlockOffset(entry);

                sum += float3(depth, (offset.x * depth), (offset.y * depth));
            }
        }
        sdfBlockSums[3u * lane] = sum.x;
        sdfBlockSums[(3u * lane) + 1u] = sum.y;
        sdfBlockSums[(3u * lane) + 2u] = sum.z;
        GroupMemoryBarrierWithGroupSync();

        // The least-squares plane of a square of texels about its centre: the mean, and each axis's moment over the sum
        // of the squared offsets, which is the texel count times (edge squared less one) over twelve.
        if (lane == 0u) {
            float3 total = float3(0.0, 0.0, 0.0);

            [loop] for (uint added = 0u; (added < SDF_BLOCK_LANES); added++) {
                total += float3(sdfBlockSums[3u * added], sdfBlockSums[(3u * added) + 1u], sdfBlockSums[(3u * added) + 2u]);
            }

            float count = float(SDF_SHADOW_BLOCK_EDGE * SDF_SHADOW_BLOCK_EDGE);
            float moment = ((count * float((SDF_SHADOW_BLOCK_EDGE * SDF_SHADOW_BLOCK_EDGE) - 1u)) / 12.0);

            sdfBlockPlane[0] = (total.x / count);
            sdfBlockPlane[1] = (total.y / moment);
            sdfBlockPlane[2] = (total.z / moment);
        }
        GroupMemoryBarrierWithGroupSync();

        float3 plane = float3(sdfBlockPlane[0], sdfBlockPlane[1], sdfBlockPlane[2]);
        float low = 3.402823e+38;
        float high = -3.402823e+38;

        [loop] for (entry = lane; (entry < SDF_BLOCK_AREA); entry += SDF_BLOCK_LANES) {
            float depth = sdfBlockDepths[entry];

            if (depth >= 0.0) {
                float residual = (depth - (plane.x + dot(plane.yz, sdfBlockOffset(entry))));

                low = min(low, residual);
                high = max(high, residual);
            }
        }
        sdfBlockLow[lane] = low;
        sdfBlockHigh[lane] = high;
        GroupMemoryBarrierWithGroupSync();

        if (lane == 0u) {
            [loop] for (uint joined = 1u; (joined < SDF_BLOCK_LANES); joined++) {
                low = min(low, sdfBlockLow[joined]);
                high = max(high, sdfBlockHigh[joined]);
            }

            uint first = sdfShadowBlockWord(side, block, (layer == 1u));
            // A record whose plane or bounds are not finite numbers in order says nothing.
            bool sound = (all(isfinite(plane)) && (low <= high) && isfinite(low) && isfinite(high));

            shadowBlocksRW[first] = asuint(plane.x);
            shadowBlocksRW[first + 1u] = asuint(plane.y);
            shadowBlocksRW[first + 2u] = asuint(plane.z);
            shadowBlocksRW[first + SdfShadowBlockLowWord] = asuint(low - SdfShadowBlockSlack);
            shadowBlocksRW[first + SdfShadowBlockHighWord] = asuint(high + SdfShadowBlockSlack);
            shadowBlocksRW[first + SdfShadowBlockFlagsWord] = ((sound ? SdfShadowBlockReduced : 0u) | glass);
        }
    }
}
