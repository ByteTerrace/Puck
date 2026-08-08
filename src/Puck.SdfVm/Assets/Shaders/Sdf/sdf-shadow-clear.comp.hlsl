// Zeroes the area-light shadow estimator's temporal history buffer (sdf-world.hlsli's sdfShadowHistory), one
// invocation per word.
//
// WHY A KERNEL AND NOT A HOST WRITE. The history is device-local — it is read AND written per lit pixel every frame,
// so a host-visible allocation would put that traffic on the PCIe bus. Device-local memory has no contents guarantee
// on either backend, and the accumulator's replay contract is BYTE-IDENTICAL history across two runs of the same
// frame sequence: an entry whose bits are whatever the allocator left behind is exactly what that contract forbids.
// So the engine dispatches this once, before the first frame of a freshly constructed engine, and thereafter resets
// accumulation through the EPOCH in the push block rather than by clearing again. Zero is the correct baseline
// because bit 30 (written) is clear, which is the one test every history read starts from.
//
// It shares Stage 0/1's push block verbatim (SdfWorldEngine.m_pushConstant), so the layout is declared in full even
// though only imageExtent is read — the root signature is one shape across the pipelines that bind it.
struct CompositeParams {
    uint2 imageExtent;
    uint2 tileGrid;
    uint viewportCount;
    uint childMask;
    uint screenMask;
    uint instanceMaskWordCount;
    uint sampleIndex;
    uint shadowEpoch;
};
[[vk::push_constant]] ConstantBuffer<CompositeParams> params;

// Binding 49 as everywhere else; register(u0) because this kernel binds NOTHING else, and Direct3D 12 assigns the UAV
// registers positionally from the engine's per-pipeline bindings array. KEEP IN SYNC with
// SdfWorldEngine.ShadowHistoryBindingIndex.
[[vk::binding(49, 0)]] RWStructuredBuffer<uint> sdfShadowHistory : register(u0);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    // One entry per native output pixel, two words each.
    uint wordCount = ((params.imageExtent.x * params.imageExtent.y) * 2u);

    if (id.x >= wordCount) {
        return;
    }

    sdfShadowHistory[id.x] = 0u;
}
