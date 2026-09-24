// The table uploader — copies the changed word ranges of a host-written table (the viewport rows, the dynamic
// transforms, the frame instance grid) from its host-visible ring-slot buffer into the persistent device-local table
// every march kernel binds, ONE thread per copied uint and ONE dispatch per table. It is the sibling of
// sdf-brick-upload.comp (same binding shape: t0 source, u0 destination) and exists so no march kernel reads those
// tables out of host-visible memory — the instance-cull walk per tile, the soft-shadow gather per lit pixel, and
// mapCore's dynamic-transform fetch on every dynamic-instance evaluation all read device memory. Words outside every
// range keep what earlier frames copied. Bit-exact: uints move as uints, whatever the table stores in them.
//
// Source layout (SdfWorldEngine.Uploads.cs): [run table][table], the table starting at tableBase. Thread i copies
// destination[word] = source[tableBase + word]. With one run, word = offset + i. With two or more, the run table holds
// (table offset, prefix) per run, prefix being the run's first thread index, and thread i binary-searches the last run
// whose prefix is at most i.
// KEEP IN SYNC with SdfWorldEngine.RecordFrameUpload's push words and WriteOwedWords' run table.

[[vk::binding(0, 0)]] StructuredBuffer<uint> uploadSource : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> uploadDestination : register(u0);

struct FrameUploadPush {
    uint count;     // uints to copy, summed over every run
    uint runCount;  // runs; the run table is read only when there are two or more
    uint offset;    // the single run's first uint
    uint tableBase; // where the table starts in the source, past the run-table reserve
};
[[vk::push_constant]] FrameUploadPush uploadPush;

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = id.x;

    if (i >= uploadPush.count) {
        return;
    }

    uint word = (uploadPush.offset + i);

    if (uploadPush.runCount > 1) {
        uint low = 0;
        uint high = (uploadPush.runCount - 1);

        [loop]
        while (low < high) {
            uint middle = ((low + high + 1) >> 1);

            if (uploadSource[((middle * 2) + 1)] <= i) {
                low = middle;
            } else {
                high = (middle - 1);
            }
        }

        word = (uploadSource[(low * 2)] + (i - uploadSource[((low * 2) + 1)]));
    }

    uploadDestination[word] = uploadSource[(uploadPush.tableBase + word)];
}
