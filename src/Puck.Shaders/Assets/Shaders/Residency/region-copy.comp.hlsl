// The region copy — moves the owed word ranges of a host-written block from its host-visible staging buffer into the
// buffer GPU work reads, one dispatch per block. It is the staged policy's copy for every GpuRegion owner: the SDF
// engine's per-frame regions, its mesh region, and its brick staging, whose destination is the brick pool. One pipeline
// serves a device (GpuRegionCopyPass). Words outside every range keep what earlier copies wrote. Bit-exact: uints move
// as uints, whatever the block stores in them.
//
// Source layout: a four-uint header (count, runCount, blockBase, destinationBase), then runCount (region word, first
// thread) pairs, then the block at blockBase holding every run's words packed one after another. Thread t is its
// dispatch row times RegionCopyRowThreads plus its column (a copy past one row of 65,535 groups dispatches further rows,
// at most RegionCopyMaxRows of them, since Direct3D 12's GPU-based validation refuses more than 4,194,303 groups). It
// copies every block word i below count from t in steps of the dispatch's threads: it binary-searches the last run whose
// first thread is at most i, and copies destination[destinationBase + word] = source[blockBase + i], word being that
// run's region word plus i minus its first thread. No push constants: the staging buffer states its copy, and the
// dispatch's threads follow from its count as GpuRegion.CopyThreads computes them.
// KEEP IN SYNC with GpuRegion (its Copy* constants, CopyGroups, CopyThreads, and StageOwed's header and run table). Each
// register number equals its binding (GpuRegisterNumbering.Binding).

[[vk::binding(0, 0)]] StructuredBuffer<uint> copySource : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> copyDestination : register(u1);

static const uint RegionCopyHeaderWords = 4;
static const uint RegionCopyGroupsPerRow = 65535;
static const uint RegionCopyRowThreads = (RegionCopyGroupsPerRow * 64);
static const uint RegionCopyMaxRows = 64;

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint count = copySource[0];
    uint groups = ((count + 63) / 64);
    uint threads = ((groups <= RegionCopyGroupsPerRow)
        ? (groups * 64)
        : (RegionCopyRowThreads * min(((groups + (RegionCopyGroupsPerRow - 1)) / RegionCopyGroupsPerRow), RegionCopyMaxRows)));

    [loop]
    for (uint i = ((id.y * RegionCopyRowThreads) + id.x); (i < count); i += threads) {
        uint low = 0;
        uint high = (copySource[1] - 1);

        [loop]
        while (low < high) {
            uint middle = ((low + high + 1) >> 1);

            if (copySource[(RegionCopyHeaderWords + (middle * 2) + 1)] <= i) {
                low = middle;
            } else {
                high = (middle - 1);
            }
        }

        uint run = (RegionCopyHeaderWords + (low * 2));
        uint word = (copySource[run] + (i - copySource[(run + 1)]));

        copyDestination[(copySource[3] + word)] = copySource[(copySource[2] + i)];
    }
}
