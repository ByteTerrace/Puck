// The frame uploader — copies one of this frame's host-written per-frame tables (the viewport rows, the dynamic
// transforms, the frame instance grid) from its host-visible ring-slot buffer into its device-local twin, ONE thread
// per uint. It is the per-frame sibling of sdf-brick-upload.comp (same binding shape: t0 source, u0 destination) and
// exists for one reason: every march kernel used to read those tables straight out of host-visible memory — the
// instance-cull walk per tile, the soft-shadow gather per lit pixel, and mapCore's dynamic-transform fetch on every
// dynamic-instance evaluation — which on a discrete GPU is a PCIe round trip per fetch. After this copy the kernels
// bind the twins and read device memory. Bit-exact: uints move as uints, whatever the table stores in them.
//
// Push constants carry the window: destination[i] = source[i] for i in [0, count).

[[vk::binding(0, 0)]] StructuredBuffer<uint> uploadSource : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> uploadDestination : register(u0);

struct FrameUploadPush {
    uint count; // uints to copy
    uint pad0;
    uint pad1;
    uint pad2;
};
[[vk::push_constant]] FrameUploadPush uploadPush;

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = id.x;

    if (i >= uploadPush.count) {
        return;
    }

    uploadDestination[i] = uploadSource[i];
}
