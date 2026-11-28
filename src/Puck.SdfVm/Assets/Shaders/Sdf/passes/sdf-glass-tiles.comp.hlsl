// The glass tiles' upkeep (SdfKernel.GlassTiles) on the glass interface, three steps the pushed index chooses
// (SdfGlassTilesRecorder), each costing the tiles the glass covers rather than the image:
// - SdfGlassTilesClearSums, from the glass clear: each group zeroes the glass sums of listed tiles, a tile a group at a
//   time striding by the groups dispatched (passGroup.glassJobs.y in the clear's block), so every sums word is zero
//   again before the raster accumulates (frame/sdf-glass-tiles.hlsli). A lane counts a texel for each pixel it clears.
// - SdfGlassTilesClearMasks, from the glass clear once the first step has read the list: zeroes the header and both
//   masks, an invocation a word, so the raster marks from nothing and no tile shows before the shading pass says so. The
//   list and the sky filter keep their words, which nothing reads past the count or outside a shown tile.
// - SdfGlassTilesList, from the glass tiles pass, one group: compacts the marked mask into the tile list in increasing
//   tile order, a chunk of mask words at a time (a word a lane, its bits placed after the earlier lanes' by a group scan
//   of the words' bit counts), writes the count, then the glass shading pass's indirect arguments, a record a job of the
//   plan (passGroup.glassJobs: the tiles a job dispatches at most and the jobs), each the listed tiles past the job's
//   first, at most a job's width, so the shading pass dispatches a group for each listed tile and none for any other.
//   It counts a texel for each pixel of a listed tile.
#include "../isa/sdf-glass.interface.hlsli"
#include "../frame/sdf-glass-tiles.hlsli"

static const uint SdfGlassTilesClearSums = 0u;
static const uint SdfGlassTilesClearMasks = 1u;
static const uint SdfGlassTilesList = 2u;
// The invocations of a group, SdfGlassTilesRecorder.GroupSize on the host.
static const uint SdfGlassTilesGroupSize = 256u;
static const uint SdfGlassTileSumWords = (SDF_GLASS_TILE_PIXELS * SDF_GLASS_SUM_WORDS);

// The list step's scan of a chunk's bit counts.
groupshared uint sdfGlassTilesScan[SdfGlassTilesGroupSize];

[numthreads(256, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID, uint3 lane : SV_GroupThreadID) {
    uint step = pushedIndex.index;
    uint words;
    uint stride;

    sdfGlassTilesRW.GetDimensions(words, stride);

    uint capacity = sdfGlassTileCapacity(words);
    uint texels = 0u;

    if (step == SdfGlassTilesClearSums) {
        uint dispatched = max(passGroup.glassJobs.y, 1u);
        uint count = min(sdfGlassTilesRW[0], capacity);

        [loop]
        for (uint entry = group.x; (entry < count); entry += dispatched) {
            uint tile = sdfGlassTilesRW[sdfGlassTileListWord(capacity, entry)];

            // The sums hold as many tiles as the tiles buffer has room for; the list names none past it.
            if (tile >= capacity) {
                continue;
            }

            uint first = (tile * SdfGlassTileSumWords);

            [loop]
            for (uint word = lane.x; (word < SdfGlassTileSumWords); word += SdfGlassTilesGroupSize) {
                sdfGlassSumsClearRW[(first + word)] = 0u;
            }
            texels += ((lane.x < SDF_GLASS_TILE_PIXELS) ? 1u : 0u);
        }
    } else if (step == SdfGlassTilesClearMasks) {
        // The host dispatches an invocation a word the clear zeroes (SdfWorldPackage.GlassTileClearWords).
        if (id.x < sdfGlassTileClearWords(capacity)) {
            sdfGlassTilesRW[sdfGlassTileClearWord(capacity, id.x)] = 0u;
        }
    } else if (step == SdfGlassTilesList) {
        uint maskWords = sdfGlassTileMaskWords(capacity);
        uint listed = 0u;

        [loop]
        for (uint base = 0u; (base < maskWords); base += SdfGlassTilesGroupSize) {
            uint word = (base + lane.x);
            uint bits = ((word < maskWords) ? sdfGlassTilesRW[sdfGlassTileMarkedWord(word * 32u)] : 0u);
            uint bitCount = countbits(bits);

            // An inclusive scan of the chunk's bit counts, uniform across the group.
            sdfGlassTilesScan[lane.x] = bitCount;
            GroupMemoryBarrierWithGroupSync();
            [unroll]
            for (uint reach = 1u; (reach < SdfGlassTilesGroupSize); reach <<= 1u) {
                uint before = ((lane.x >= reach) ? sdfGlassTilesScan[(lane.x - reach)] : 0u);

                GroupMemoryBarrierWithGroupSync();
                sdfGlassTilesScan[lane.x] += before;
                GroupMemoryBarrierWithGroupSync();
            }

            uint entry = ((listed + sdfGlassTilesScan[lane.x]) - bitCount);

            [loop]
            while (bits != 0u) {
                sdfGlassTilesRW[sdfGlassTileListWord(capacity, entry)] = ((word * 32u) + firstbitlow(bits));
                entry++;
                bits &= (bits - 1u);
            }
            listed += sdfGlassTilesScan[(SdfGlassTilesGroupSize - 1u)];
            // The next chunk's scan overwrites what every lane has just read.
            GroupMemoryBarrierWithGroupSync();
        }
        if (lane.x == 0u) {
            sdfGlassTilesRW[0] = listed;
            texels = (listed * SDF_GLASS_TILE_PIXELS);
        }

        uint width = max(passGroup.glassJobs.x, 1u);
        uint jobs = passGroup.glassJobs.y;

        [loop]
        for (uint job = lane.x; (job < jobs); job += SdfGlassTilesGroupSize) {
            uint firstTile = (job * width);

            sdfGlassArgumentsRW[(3u * job)] = ((listed > firstTile) ? min((listed - firstTile), width) : 0u);
            sdfGlassArgumentsRW[((3u * job) + 1u)] = 1u;
            sdfGlassArgumentsRW[((3u * job) + 2u)] = 1u;
        }
    }
    puckCountWork(0u, texels);
}
