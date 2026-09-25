// The region copy — moves the owed word ranges of a host-written block from its host-visible staging buffer into the
// device-local buffer GPU work reads, one thread per copied uint and one dispatch per block. It is the staged policy's
// copy for every GpuRegion owner, and the SDF engine's table upload records it for its viewport rows, dynamic
// transforms and frame instance grid. One pipeline serves a device (GpuRegionCopyPipelineCache). Words outside every
// range keep what earlier copies wrote. Bit-exact: uints move as uints, whatever the block stores in them.
//
// Source layout: [run table][block], the block starting at tableBase. Thread i copies
// destination[word] = source[tableBase + word]. With one run, word = offset + i. With two or more, the run table holds
// (block offset, prefix) per run, prefix being the run's first thread index, and thread i binary-searches the last run
// whose prefix is at most i.
// KEEP IN SYNC with GpuRegion (its Copy* constants, RecordCopy's push words and StageOwed's run table) and
// SdfWorldEngine.RecordFrameUpload. Each register number equals its binding (GpuRegisterNumbering.Binding).

[[vk::binding(0, 0)]] StructuredBuffer<uint> copySource : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> copyDestination : register(u1);

struct RegionCopyPush {
    uint count;     // uints to copy, summed over every run
    uint runCount;  // runs; the run table is read only when there are two or more
    uint offset;    // the single run's first uint
    uint tableBase; // where the block starts in the source, past the run-table reserve
};
[[vk::push_constant]] RegionCopyPush copyPush;

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = id.x;

    if (i >= copyPush.count) {
        return;
    }

    uint word = (copyPush.offset + i);

    if (copyPush.runCount > 1) {
        uint low = 0;
        uint high = (copyPush.runCount - 1);

        [loop]
        while (low < high) {
            uint middle = ((low + high + 1) >> 1);

            if (copySource[((middle * 2) + 1)] <= i) {
                low = middle;
            } else {
                high = (middle - 1);
            }
        }

        word = (copySource[(low * 2)] + (i - copySource[((low * 2) + 1)]));
    }

    copyDestination[word] = copySource[(copyPush.tableBase + word)];
}
