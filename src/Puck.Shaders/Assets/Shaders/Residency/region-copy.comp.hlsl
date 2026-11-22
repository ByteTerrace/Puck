// The shared buffer copy — moves packed host data or selected rows between strided GPU buffers, one dispatch per request.
// It is the staged policy's copy for every GpuRegion owner: the SDF
// engine's per-frame regions and its mesh region. One pipeline
// serves a device (GpuRegionCopyPass). Words outside every range keep what earlier copies wrote. Bit-exact: uints move
// as uints, whatever the block stores in them.
//
// Request layout: seven uints (count, runCount, sourceBase, destinationBase, rowWords, sourceStride, destinationStride),
// then runCount (logical word, first thread) pairs. With rowWords zero, the source holds packed data from sourceBase (a
// region's block of its staging ring). With rowWords nonzero, logical word / rowWords selects a row and % rowWords selects
// its column, mapped through sourceStride and destinationStride. Source data is a separate binding. With rowWords zero
// and sourceStride nonzero, runCount source words follow the run table and each run reads from its own: with
// RegionCopyMoveMode from the destination at destinationBase plus its source word (a move within the destination), with
// RegionCopyGatherMode from the source at sourceBase plus its source word (a gather from a packed buffer).
// Thread t is its
// dispatch row times RegionCopyRowThreads plus its column (a copy past one row of 65,535 groups dispatches further rows,
// at most RegionCopyMaxRows of them, since Direct3D 12's GPU-based validation refuses more than 4,194,303 groups). It
// copies every block word i below count from t in steps of the dispatch's threads: it binary-searches the last run whose
// first thread is at most i, and in packed mode copies destination[destinationBase + word] = source[sourceBase + i], word being that
// run's region word plus i minus its first thread. No push constants: the request states its copy, and the
// dispatch's threads follow from its count as GpuRegion.CopyThreads computes them.
// KEEP IN SYNC with GpuRegion (its Copy* constants, CopyGroups, CopyThreads, and StageOwed's header and run table). Each
// register number equals its binding (GpuRegisterNumbering.Binding).

[[vk::binding(0, 0)]] StructuredBuffer<uint> copyRequest : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> copyDestination : register(u1);
[[vk::binding(2, 0)]] StructuredBuffer<uint> copySource : register(t2);

static const uint RegionCopyHeaderWords = 7;
static const uint RegionCopyGroupsPerRow = 65535;
static const uint RegionCopyRowThreads = (RegionCopyGroupsPerRow * 64);
static const uint RegionCopyMaxRows = 64;
// A packed request's (rowWords zero) sixth word: a move within the destination (GpuRegion.CopyMoveMode), or a gather from
// the source with a source word per run (GpuRegion.CopyGatherMode, 2).
static const uint RegionCopyMoveMode = 1;

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint count = copyRequest[0];
    uint groups = ((count + 63) / 64);
    uint threads = ((groups <= RegionCopyGroupsPerRow)
        ? (groups * 64)
        : (RegionCopyRowThreads * min(((groups + (RegionCopyGroupsPerRow - 1)) / RegionCopyGroupsPerRow), RegionCopyMaxRows)));

    [loop]
    for (uint i = ((id.y * RegionCopyRowThreads) + id.x); (i < count); i += threads) {
        uint low = 0;
        uint high = (copyRequest[1] - 1);

        [loop]
        while (low < high) {
            uint middle = ((low + high + 1) >> 1);

            if (copyRequest[(RegionCopyHeaderWords + (middle * 2) + 1)] <= i) {
                low = middle;
            } else {
                high = (middle - 1);
            }
        }

        uint run = (RegionCopyHeaderWords + (low * 2));
        uint word = (copyRequest[run] + (i - copyRequest[(run + 1)]));
        uint sourceWord = copyRequest[2] + i;
        uint destinationWord = copyRequest[3] + word;
        uint rowWords = copyRequest[4];
        if (rowWords != 0u) {
            uint row = word / rowWords;
            uint column = word % rowWords;
            sourceWord = copyRequest[2] + row * copyRequest[5] + column;
            destinationWord = copyRequest[3] + row * copyRequest[6] + column;
        } else if (copyRequest[5] != 0u) {
            // A run with its own source word, which follows the run table: a move reads the destination itself (no move
            // of one request reads a word another writes, GpuRegion.TryOweMove), a gather the source binding.
            uint runSource = (copyRequest[(RegionCopyHeaderWords + (copyRequest[1] * 2) + low)] + (i - copyRequest[(run + 1)]));

            if (copyRequest[5] == RegionCopyMoveMode) {
                copyDestination[destinationWord] = copyDestination[(copyRequest[3] + runSource)];
            } else {
                copyDestination[destinationWord] = copySource[(copyRequest[2] + runSource)];
            }
            continue;
        }

        copyDestination[destinationWord] = copySource[sourceWord];
    }
}
