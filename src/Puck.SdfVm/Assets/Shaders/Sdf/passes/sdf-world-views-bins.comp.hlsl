// The bins pass of a binned view (SdfWorldPackage.Parts.ViewsBins): two dispatches the pushed index chooses between,
// over the shade bins the filter pass counted (sdf-views-filter.hlsli). Step zero, one group, turns each bin's tile count
// into its first entry in the tile list and zeroes the scatter's cursors. Step one, an invocation a tile, writes each tile
// into its bin's range of the tile list, in no particular order within a bin, and writes the indirect arguments of the
// job of each bin its index names, a job of a bin's tiles past its count dispatching nothing.
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-filter.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint3 lane : SV_GroupThreadID) {
    uint viewIndex = worldViewOf(0u);
    uint base = sdfShadeBinsBase(viewIndex);

    if (pushedIndex.index == 0u) {
        if (lane.x == 0u) {
            uint offset = 0u;

            for (uint bin = 0u; (bin < SDF_SHADE_BINS); bin++) {
                uint count = sdfShadeTilesRW[(base + SdfShadeCountWord) + bin];

                sdfShadeTilesRW[(base + SdfShadeOffsetWord) + bin] = offset;
                sdfShadeTilesRW[(base + SdfShadeCursorWord) + bin] = 0u;
                offset += count;
            }
        }
        // Each recorded bin's tiles, and the pixels they cover, into the pass's detail row of that bin
        // (SdfWorldPassRecorder.WorkDetails labels a row for every bin).
        if ((lane.x < SDF_SHADE_BINS) && (sdfShadeBinWidth(lane.x) != 0u)) {
            uint count = sdfShadeTilesRW[(base + SdfShadeCountWord) + lane.x];

            puckCountDetail(lane.x, count, (count * (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE)), 0u, 0u, 0u);
        }
        return;
    }

    uint tiles = sdfShadeTileCount();

    // A tile an invocation, striding by the most invocations one dispatch axis may hold (65535 groups of 64), so an
    // extent past that many tiles still scatters every tile; the host dispatches at most that many groups.
    for (uint tile = id.x; (tile < tiles); tile += (65535u * 64u)) {
        uint tileBin = min(sdfShadeTilesRW[sdfShadeTileBinWord(viewIndex, tile)], (SDF_SHADE_BINS - 1u));
        uint slot;

        InterlockedAdd(sdfShadeTilesRW[(base + SdfShadeCursorWord) + tileBin], 1u, slot);
        sdfShadeTilesRW[sdfShadeListWord(viewIndex, (sdfShadeTilesRW[(base + SdfShadeOffsetWord) + tileBin] + slot))] = tile;

        // The invocation's index is a job of every bin: its group count is the bin's tiles past the job's first, at most the
        // bin's width. The host records each bin's jobs up to the extent's tiles at the bin's width (SdfShadeDispatch).
        for (uint bin = 0u; (bin < SDF_SHADE_BINS); bin++) {
            uint width = sdfShadeBinWidth(bin);

            if ((width == 0u) || (tile >= (((tiles + width) - 1u) / width))) {
                continue;
            }

            uint first = (tile * width);
            uint count = sdfShadeTilesRW[(base + SdfShadeCountWord) + bin];
            uint word = sdfShadeArgumentWord(viewIndex, bin, tile);

            sdfShadeArgumentsRW[word] = ((count > first) ? min((count - first), width) : 0u);
            sdfShadeArgumentsRW[word + 1u] = 1u;
            sdfShadeArgumentsRW[word + 2u] = 1u;
        }
    }
}
