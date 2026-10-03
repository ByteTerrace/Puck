// Generated from shader interface 'sdf-mesh' (sha256/e329b713c8b4879a92fa40d15daf5473d36984ac4d19bd5895248194ac191217). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_MESH
#define PUCK_SHADER_INTERFACE_SDF_MESH

// The Pass group: descriptor set 3, register space 3.
struct SdfMeshPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float aspectRatio;
    [[vk::offset(12)]] uint cameraTileShadowMask;
    [[vk::offset(16)]] float curvatureCavity;
    [[vk::offset(20)]] float curvatureInk;
    [[vk::offset(24)]] uint _pad24;
    [[vk::offset(28)]] uint _pad28;
    [[vk::offset(32)]] float3 curvatureInkColor;
    [[vk::offset(44)]] float curvatureInkHigh;
    [[vk::offset(48)]] float curvatureInkLow;
    [[vk::offset(52)]] float curvatureRim;
    [[vk::offset(56)]] uint debugMode;
    [[vk::offset(60)]] float debugSliceAxis;
    [[vk::offset(64)]] float debugSliceOffset;
    [[vk::offset(68)]] uint disableAmbientOcclusion;
    [[vk::offset(72)]] uint disableFarBound;
    [[vk::offset(76)]] uint disableScreenLights;
    [[vk::offset(80)]] uint disableShadowCull;
    [[vk::offset(84)]] uint disableSoftShadows;
    [[vk::offset(88)]] uint enableShadowProxy;
    [[vk::offset(92)]] float farDistance;
    [[vk::offset(96)]] uint fastAmbientOcclusion;
    [[vk::offset(100)]] uint fastSoftShadowMarch;
    [[vk::offset(104)]] uint finiteDifferenceNormals;
    [[vk::offset(108)]] uint _pad108;
    [[vk::offset(112)]] float2 frustumOffset;
    [[vk::offset(120)]] uint gridFlags;
    [[vk::offset(124)]] float gridLineWidth;
    [[vk::offset(128)]] float4 gridObjectFrame;
    [[vk::offset(144)]] float3 gridObjectOrigin;
    [[vk::offset(156)]] float gridObjectPatchRadius;
    [[vk::offset(160)]] float3 gridObjectPitch;
    [[vk::offset(172)]] float gridPlaneY;
    [[vk::offset(176)]] float4 gridWorldFrame;
    [[vk::offset(192)]] float3 gridWorldOrigin;
    [[vk::offset(204)]] uint _pad204;
    [[vk::offset(208)]] float3 gridWorldPitch;
    [[vk::offset(220)]] uint historyFrames;
    [[vk::offset(224)]] uint2 imageExtent;
    [[vk::offset(232)]] uint instanceMaskWordCount;
    [[vk::offset(236)]] uint _pad236;
    [[vk::offset(240)]] float2 jitter;
    [[vk::offset(248)]] uint lightCount;
    [[vk::offset(252)]] uint meshDraws;
    [[vk::offset(256)]] float nearDistance;
    [[vk::offset(260)]] uint _pad260;
    [[vk::offset(264)]] uint _pad264;
    [[vk::offset(268)]] uint _pad268;
    [[vk::offset(272)]] float4 previousView[6];
    [[vk::offset(368)]] uint screenCount;
    [[vk::offset(372)]] float shadowDistanceScale;
    [[vk::offset(376)]] uint shadowFadeCount;
    [[vk::offset(380)]] uint shadowSlotCount;
    [[vk::offset(384)]] int4 shadowSlots;
    [[vk::offset(400)]] float tanHalfFieldOfView;
    [[vk::offset(404)]] uint temporal;
    [[vk::offset(408)]] uint2 tileGrid;
    [[vk::offset(416)]] uint viewBase;
    [[vk::offset(420)]] uint _pad420;
    [[vk::offset(424)]] uint _pad424;
    [[vk::offset(428)]] uint _pad428;
    [[vk::offset(432)]] float3 viewForward;
    [[vk::offset(444)]] uint _pad444;
    [[vk::offset(448)]] float3 viewPosition;
    [[vk::offset(460)]] uint _pad460;
    [[vk::offset(464)]] float3 viewRight;
    [[vk::offset(476)]] uint _pad476;
    [[vk::offset(480)]] float3 viewUp;
    [[vk::offset(492)]] uint viewportCount;
    [[vk::offset(496)]] uint workCounterRow;
    [[vk::offset(500)]] uint workCounterRowDetail;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfMeshPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] Texture2D<float4> sdfImpostorDepth : register(t2, space3);
[[vk::binding(3, 3)]] RWStructuredBuffer<uint> workCounters : register(u3, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfMeshPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfMeshPushedIndex> pushedIndex : register(b0, space4);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, then six shadow-slot step counts, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 22u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShadowWord = 10u;
static const uint PuckWorkShadowSlots = 6u;
// Adds to one count: the low word atomically, then the high word by one when that addition carries.
void puckAddWork(uint word, uint amount) {
    if (amount == 0u) {
        return;
    }

    uint before;

    InterlockedAdd(workCounters[word], amount, before);

    if (before > (0xFFFFFFFFu - amount)) {
        InterlockedAdd(workCounters[word + 1u], 1u);
    }
}
// Adds an invocation's march steps and texels written to its pass's row: the wave sums both, and its first active
// lane adds each sum. Every lane that did work reaches the call, since a lane that returned before it counts nothing.
void puckCountWork(uint steps, uint texels) {
    uint waveSteps = WaveActiveSum(steps);
    uint waveTexels = WaveActiveSum(texels);

    if (WaveIsFirstLane()) {
        uint row = (passGroup.workCounterRow * PuckWorkRowWords);

        puckAddWork((row + PuckWorkStepsWord), waveSteps);
        puckAddWork((row + PuckWorkTexelsWord), waveTexels);
    }
}
// Named rows are disjoint from the plain pass row; the ledger sums both once the submission completes.
// Per-lane atomics permit divergent layer evaluation without merging lanes targeting different rows.
void puckCountDetail(uint detail, uint steps, uint texels, uint evaluations, uint hashes, uint loads) {
    if (passGroup.workCounterRowDetail == 0u) {
        return;
    }
    uint row = ((passGroup.workCounterRowDetail + detail) * PuckWorkRowWords);
    puckAddWork((row + PuckWorkStepsWord), steps);
    puckAddWork((row + PuckWorkTexelsWord), texels);
    puckAddWork((row + PuckWorkSkyWord), evaluations);
    puckAddWork((row + PuckWorkSkyHashesWord), hashes);
    puckAddWork((row + PuckWorkSkyTextureLoadsWord), loads);
}
// The slot is uniform across the wave. Stable slots precede active handoffs in the shadow pass's row.
void puckCountShadow(uint slot, uint steps) {
    uint waveSteps = WaveActiveSum(steps);

    if ((slot < PuckWorkShadowSlots) && WaveIsFirstLane()) {
        puckAddWork(((passGroup.workCounterRow * PuckWorkRowWords) + PuckWorkShadowWord + (slot * 2u)), waveSteps);
    }
}
// Adds a fragment's march steps and texels written to its pass's row: the wave sums its lanes that are not helper
// lanes, and the first of them adds each sum. A helper lane counts nothing and never adds, whether or not the
// backend lets it take part in wave operations, since its atomics have no effect.
void puckCountFragmentWork(uint steps, uint texels) {
    bool counting = !IsHelperLane();
    uint waveSteps = WaveActiveSum(counting ? steps : 0u);
    uint waveTexels = WaveActiveSum(counting ? texels : 0u);

    if (counting && (WavePrefixCountBits(counting) == 0u)) {
        uint row = (passGroup.workCounterRow * PuckWorkRowWords);

        puckAddWork((row + PuckWorkStepsWord), waveSteps);
        puckAddWork((row + PuckWorkTexelsWord), waveTexels);
    }
}

#endif // PUCK_SHADER_INTERFACE_SDF_MESH
