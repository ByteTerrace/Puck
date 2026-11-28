// The glass tiles' upkeep (SdfKernel.GlassTiles) on the glass interface, three steps the pushed index chooses
// (SdfGlassTilesRecorder), each costing the tiles the glass covers rather than the image:
// - SdfGlassTilesClearSums, from the glass clear: each group zeroes the glass sums of listed tiles, a tile a group at a
//   time striding by the groups dispatched (passGroup.glassJobs.y in the clear's block), so every sums word is zero
//   again before the raster accumulates (frame/sdf-glass-tiles.hlsli). A lane counts a texel for each pixel it clears.
// - SdfGlassTilesClearMask, from the glass clear once the first step has read the list: zeroes the header and the mask,
//   an invocation a word, so the raster marks and lists from nothing. The list and the sky filter keep their words, which nothing reads past
//   the count or outside a marked tile.
// - SdfGlassTilesArguments, from the glass tiles pass, one group: the glass shading pass's indirect arguments, a record a
//   job of the plan (passGroup.glassJobs: the tiles a job dispatches at most and the jobs), each the listed tiles past the
//   job's first, at most a job's width, so the shading pass dispatches a group for each listed tile and none for any other.
#include "../isa/sdf-glass.interface.hlsli"
#include "../frame/sdf-glass-tiles.hlsli"

static const uint SdfGlassTilesClearSums = 0u;
static const uint SdfGlassTilesClearMask = 1u;
static const uint SdfGlassTilesArguments = 2u;
static const uint SdfGlassTilesGroupSize = 64u;
static const uint SdfGlassTileSumWords = (SDF_GLASS_TILE_PIXELS * SDF_GLASS_SUM_WORDS);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID, uint3 lane : SV_GroupThreadID) {
    uint step = pushedIndex.index;
    uint texels = 0u;

    if (step == SdfGlassTilesClearSums) {
        uint words;
        uint stride;
        uint dispatched = max(passGroup.glassJobs.y, 1u);

        sdfGlassTilesRW.GetDimensions(words, stride);

        uint capacity = sdfGlassTileCapacity(words);
        uint count = min(sdfGlassTilesRW[0], capacity);

        [loop]
        for (uint entry = group.x; (entry < count); entry += dispatched) {
            uint tile = sdfGlassTilesRW[sdfGlassTileListWord(capacity, entry)];

            // The sums hold as many tiles as the tiles buffer has room for; the raster lists none past it.
            if (tile >= capacity) {
                continue;
            }

            uint first = (tile * SdfGlassTileSumWords);

            [loop]
            for (uint word = lane.x; (word < SdfGlassTileSumWords); word += SdfGlassTilesGroupSize) {
                sdfGlassSumsClearRW[(first + word)] = 0u;
            }
            texels++;
        }
    } else if (step == SdfGlassTilesClearMask) {
        uint words;
        uint stride;

        sdfGlassTilesRW.GetDimensions(words, stride);

        // The host dispatches an invocation a word of the header and the mask (SdfWorldPackage.GlassTileMaskEnd).
        if (id.x < min(sdfGlassTileMaskEnd(sdfGlassTileCapacity(words)), words)) {
            sdfGlassTilesRW[id.x] = 0u;
        }
    } else if (step == SdfGlassTilesArguments) {
        uint width = max(passGroup.glassJobs.x, 1u);
        uint jobs = passGroup.glassJobs.y;
        uint count = sdfGlassTiles[0];

        [loop]
        for (uint job = lane.x; (job < jobs); job += SdfGlassTilesGroupSize) {
            uint firstTile = (job * width);

            sdfGlassArgumentsRW[(3u * job)] = ((count > firstTile) ? min((count - firstTile), width) : 0u);
            sdfGlassArgumentsRW[((3u * job) + 1u)] = 1u;
            sdfGlassArgumentsRW[((3u * job) + 2u)] = 1u;
        }
    }
    puckCountWork(0u, texels);
}
