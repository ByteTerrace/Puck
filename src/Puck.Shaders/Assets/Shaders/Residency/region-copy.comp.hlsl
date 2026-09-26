// The region copy — moves the owed word ranges of a host-written block from its host-visible staging buffer into the
// buffer GPU work reads, one thread per copied uint and one dispatch per block. It is the staged policy's copy for every
// GpuRegion owner: the SDF engine's per-frame regions, its mesh region, and its brick staging, whose destination is the
// brick pool. One pipeline serves a device (GpuRegionCopyPass). Words outside every range keep what earlier
// copies wrote. Bit-exact: uints move as uints, whatever the block stores in them.
//
// Source layout: a four-uint header (count, runCount, blockBase, destinationBase), then runCount (block offset, first
// thread) pairs, then the block at blockBase. Thread i, its dispatch row times RegionCopyRowThreads plus its column (a
// copy past one row of 65,535 groups dispatches further rows), below count binary-searches the last run whose first
// thread is at most i, and copies destination[destinationBase + word] = source[blockBase + word], word being that run's block offset
// plus i minus its first thread. No push constants: the staging buffer states its copy.
// KEEP IN SYNC with GpuRegion (its Copy* constants, CopyGroups, and StageOwed's header and run table). Each register number equals
// its binding (GpuRegisterNumbering.Binding).

[[vk::binding(0, 0)]] StructuredBuffer<uint> copySource : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> copyDestination : register(u1);

static const uint RegionCopyHeaderWords = 4;
static const uint RegionCopyRowThreads = (65535 * 64);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = ((id.y * RegionCopyRowThreads) + id.x);

    if (i >= copySource[0]) {
        return;
    }

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

    copyDestination[(copySource[3] + word)] = copySource[(copySource[2] + word)];
}
