// Generated from shader interface 'sdf-mesh' (sha256/0c18a22ac643513f6a26b701bde2050a920fb4a87247cb35046150cd852d1860). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_MESH
#define PUCK_SHADER_INTERFACE_SDF_MESH

// The Pass group: descriptor set 3, register space 3.
struct SdfMeshPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float ambientScale;
    [[vk::offset(12)]] float aspectRatio;
    [[vk::offset(16)]] uint cameraTileShadowMask;
    [[vk::offset(20)]] uint debugMode;
    [[vk::offset(24)]] float debugSliceAxis;
    [[vk::offset(28)]] float debugSliceOffset;
    [[vk::offset(32)]] uint disableAmbientOcclusion;
    [[vk::offset(36)]] uint disableFarBound;
    [[vk::offset(40)]] uint disableScreenLights;
    [[vk::offset(44)]] uint disableShadowCull;
    [[vk::offset(48)]] uint disableSoftShadows;
    [[vk::offset(52)]] uint enableShadowProxy;
    [[vk::offset(56)]] uint _pad56;
    [[vk::offset(60)]] uint _pad60;
    [[vk::offset(64)]] float4 environment[53];
    [[vk::offset(912)]] float farDistance;
    [[vk::offset(916)]] uint fastAmbientOcclusion;
    [[vk::offset(920)]] uint fastSoftShadowMarch;
    [[vk::offset(924)]] uint finiteDifferenceNormals;
    [[vk::offset(928)]] float2 frustumOffset;
    [[vk::offset(936)]] uint gridFlags;
    [[vk::offset(940)]] float gridFloorY;
    [[vk::offset(944)]] float4 gridObjectFrame;
    [[vk::offset(960)]] float3 gridObjectOrigin;
    [[vk::offset(972)]] float gridObjectPatchRadius;
    [[vk::offset(976)]] float2 gridObjectPitch;
    [[vk::offset(984)]] float2 gridWorldPitch;
    [[vk::offset(992)]] uint2 imageExtent;
    [[vk::offset(1000)]] uint instanceMaskWordCount;
    [[vk::offset(1004)]] uint meshDraws;
    [[vk::offset(1008)]] float nearDistance;
    [[vk::offset(1012)]] uint sampleIndex;
    [[vk::offset(1016)]] float sceneTime;
    [[vk::offset(1020)]] uint screenCount;
    [[vk::offset(1024)]] float shadowDistanceScale;
    [[vk::offset(1028)]] float sunScale;
    [[vk::offset(1032)]] float tanHalfFieldOfView;
    [[vk::offset(1036)]] uint _pad1036;
    [[vk::offset(1040)]] uint2 tileGrid;
    [[vk::offset(1048)]] uint viewBase;
    [[vk::offset(1052)]] uint _pad1052;
    [[vk::offset(1056)]] float3 viewForward;
    [[vk::offset(1068)]] uint _pad1068;
    [[vk::offset(1072)]] float3 viewPosition;
    [[vk::offset(1084)]] uint _pad1084;
    [[vk::offset(1088)]] float3 viewRight;
    [[vk::offset(1100)]] uint _pad1100;
    [[vk::offset(1104)]] float3 viewUp;
    [[vk::offset(1116)]] uint viewportCount;
    [[vk::offset(1120)]] uint workCounterRow;
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
// back): each counted kind in GpuWork.KernelKinds order, march steps then texels written, as a 64-bit count in
// two words, low word first.
static const uint PuckWorkRowWords = 4u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
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
// Adds one invocation's own march steps and texels written to its pass's row, with no wave sum: for a fragment
// stage, whose helper lanes' atomics have no effect.
void puckCountWorkEach(uint steps, uint texels) {
    uint row = (passGroup.workCounterRow * PuckWorkRowWords);

    puckAddWork((row + PuckWorkStepsWord), steps);
    puckAddWork((row + PuckWorkTexelsWord), texels);
}

#endif // PUCK_SHADER_INTERFACE_SDF_MESH
