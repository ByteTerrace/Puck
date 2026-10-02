// Generated from shader interface 'sdf-mesh' (sha256/400c370a2e3c9783ef8f454f064ecb550c545961105a8b190d582bf0defd1b6d). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_MESH
#define PUCK_SHADER_INTERFACE_SDF_MESH

// The Pass group: descriptor set 3, register space 3.
struct SdfMeshPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float aspectRatio;
    [[vk::offset(12)]] uint cameraTileShadowMask;
    [[vk::offset(16)]] uint curvatureEnabled;
    [[vk::offset(20)]] uint debugMode;
    [[vk::offset(24)]] float debugSliceAxis;
    [[vk::offset(28)]] float debugSliceOffset;
    [[vk::offset(32)]] uint disableAmbientOcclusion;
    [[vk::offset(36)]] uint disableFarBound;
    [[vk::offset(40)]] uint disableScreenLights;
    [[vk::offset(44)]] uint disableShadowCull;
    [[vk::offset(48)]] uint disableSoftShadows;
    [[vk::offset(52)]] uint enableShadowProxy;
    [[vk::offset(56)]] float farDistance;
    [[vk::offset(60)]] uint fastAmbientOcclusion;
    [[vk::offset(64)]] uint fastSoftShadowMarch;
    [[vk::offset(68)]] uint finiteDifferenceNormals;
    [[vk::offset(72)]] float2 frustumOffset;
    [[vk::offset(80)]] uint gridFlags;
    [[vk::offset(84)]] float gridLineWidth;
    [[vk::offset(88)]] uint _pad88;
    [[vk::offset(92)]] uint _pad92;
    [[vk::offset(96)]] float4 gridObjectFrame;
    [[vk::offset(112)]] float3 gridObjectOrigin;
    [[vk::offset(124)]] float gridObjectPatchRadius;
    [[vk::offset(128)]] float3 gridObjectPitch;
    [[vk::offset(140)]] float gridPlaneY;
    [[vk::offset(144)]] float4 gridWorldFrame;
    [[vk::offset(160)]] float3 gridWorldOrigin;
    [[vk::offset(172)]] uint _pad172;
    [[vk::offset(176)]] float3 gridWorldPitch;
    [[vk::offset(188)]] uint historyFrames;
    [[vk::offset(192)]] uint2 imageExtent;
    [[vk::offset(200)]] uint instanceMaskWordCount;
    [[vk::offset(204)]] uint _pad204;
    [[vk::offset(208)]] float2 jitter;
    [[vk::offset(216)]] uint meshDraws;
    [[vk::offset(220)]] float nearDistance;
    [[vk::offset(224)]] float4 previousView[6];
    [[vk::offset(320)]] uint screenCount;
    [[vk::offset(324)]] float shadowDistanceScale;
    [[vk::offset(328)]] float tanHalfFieldOfView;
    [[vk::offset(332)]] uint _pad332;
    [[vk::offset(336)]] uint2 tileGrid;
    [[vk::offset(344)]] uint viewBase;
    [[vk::offset(348)]] uint _pad348;
    [[vk::offset(352)]] float3 viewForward;
    [[vk::offset(364)]] uint _pad364;
    [[vk::offset(368)]] float3 viewPosition;
    [[vk::offset(380)]] uint _pad380;
    [[vk::offset(384)]] float3 viewRight;
    [[vk::offset(396)]] uint _pad396;
    [[vk::offset(400)]] float3 viewUp;
    [[vk::offset(412)]] uint viewportCount;
    [[vk::offset(416)]] uint workCounterRow;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfMeshPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] RWStructuredBuffer<uint> workCounters : register(u2, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfMeshPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfMeshPushedIndex> pushedIndex : register(b0, space4);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels, sky evaluations, hashes and texture loads, as a 64-bit count in
// two words, low word first. An interface declaring no work counters declares the same two functions empty.
static const uint PuckWorkRowWords = 10u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyEvaluationsWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
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
// Counts a compute invocation's named detail. Equal active-lane rows share wave sums; mixed rows add
// independently, so divergent layer/body selection never attributes another lane's work to the first row.
// Detail rows never contain output texels: those belong to the ordinary pass/run row.
void puckCountDetailWork(uint row, uint steps, uint evaluations, uint hashes, uint textureLoads) {
    bool sharedRow = WaveActiveAllEqual(row);
    if (sharedRow) {
        steps = WaveActiveSum(steps);
        evaluations = WaveActiveSum(evaluations);
        hashes = WaveActiveSum(hashes);
        textureLoads = WaveActiveSum(textureLoads);
    }
    if (!sharedRow || WaveIsFirstLane()) {
        uint word = row * PuckWorkRowWords;
        puckAddWork(word + PuckWorkStepsWord, steps);
        puckAddWork(word + PuckWorkSkyEvaluationsWord, evaluations);
        puckAddWork(word + PuckWorkSkyHashesWord, hashes);
        puckAddWork(word + PuckWorkSkyTextureLoadsWord, textureLoads);
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
