// Generated from shader interface 'sdf-world' (sha256/0c91b30629698bf950fccc31f0d2377dc522e9a72223fa7cb28908c7f35ee0af). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_WORLD
#define PUCK_SHADER_INTERFACE_SDF_WORLD

// The Frame group: descriptor set 0, register space 0.
struct SdfWorldFrame {
    [[vk::offset(0)]] float2 pointer;
    [[vk::offset(8)]] uint2 tick;
    [[vk::offset(16)]] float time;
    [[vk::offset(20)]] float timeDelta;
    [[vk::offset(24)]] uint frame;
    [[vk::offset(28)]] uint tickRate;
    [[vk::offset(32)]] uint pointerDown;
    [[vk::offset(36)]] uint pointerPresses;
    [[vk::offset(40)]] uint _pad40;
    [[vk::offset(44)]] uint _pad44;
    [[vk::offset(48)]] float3 cameraPosition;
    [[vk::offset(60)]] float cameraFov;
    [[vk::offset(64)]] float3 cameraTarget;
    [[vk::offset(76)]] uint _pad76;
    [[vk::offset(80)]] float3 cameraUp;
};
[[vk::binding(0, 0)]] ConstantBuffer<SdfWorldFrame> frameGroup : register(b0, space0);

// The World group: descriptor set 1, register space 1.
[[vk::binding(0, 1)]] StructuredBuffer<uint4> sdfWords : register(t0, space1);
[[vk::binding(1, 1)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t1, space1);
[[vk::binding(2, 1)]] StructuredBuffer<uint> sdfFrameInstanceGrid : register(t2, space1);
[[vk::binding(3, 1)]] StructuredBuffer<float4> screenSurfaces : register(t3, space1);
[[vk::binding(4, 1)]] StructuredBuffer<float4> screenMappings : register(t4, space1);
[[vk::binding(5, 1)]] StructuredBuffer<float4> sdfScreenLights : register(t5, space1);
[[vk::binding(6, 1)]] StructuredBuffer<uint4> sdfDecalCells : register(t6, space1);
[[vk::binding(7, 1)]] StructuredBuffer<float> sdfBrickPool : register(t7, space1);
[[vk::binding(8, 1)]] StructuredBuffer<float4> sdfVolumes : register(t8, space1);
[[vk::binding(9, 1)]] StructuredBuffer<uint> sdfMeshRegion : register(t9, space1);
[[vk::binding(10, 1)]] Texture2D<float4> sdfGlyphAtlas : register(t10, space1);
[[vk::binding(11, 1)]] SamplerState samplers[2] : register(s11, space1);
[[vk::binding(13, 1)]] Texture2D<float4> sdfMeshAlbedo : register(t13, space1);
[[vk::binding(14, 1)]] Texture2D<float4> sdfMeshNormals : register(t14, space1);
[[vk::binding(15, 1)]] Texture2D<float4> sdfMeshOcclusion : register(t15, space1);
[[vk::binding(16, 1)]] Texture2D<float4> sdfMeshMaterials : register(t16, space1);
[[vk::binding(17, 1)]] Texture2D<float4> sdfMeshEmission : register(t17, space1);

// The Pass group: descriptor set 3, register space 3.
struct SdfWorldPass {
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
    [[vk::offset(940)]] float gridLineWidth;
    [[vk::offset(944)]] float4 gridObjectFrame;
    [[vk::offset(960)]] float3 gridObjectOrigin;
    [[vk::offset(972)]] float gridObjectPatchRadius;
    [[vk::offset(976)]] float3 gridObjectPitch;
    [[vk::offset(988)]] float gridPlaneY;
    [[vk::offset(992)]] float3 gridWorldPitch;
    [[vk::offset(1004)]] uint _pad1004;
    [[vk::offset(1008)]] uint2 imageExtent;
    [[vk::offset(1016)]] uint instanceMaskWordCount;
    [[vk::offset(1020)]] uint meshDraws;
    [[vk::offset(1024)]] float nearDistance;
    [[vk::offset(1028)]] uint sampleIndex;
    [[vk::offset(1032)]] float sceneTime;
    [[vk::offset(1036)]] uint screenCount;
    [[vk::offset(1040)]] float shadowDistanceScale;
    [[vk::offset(1044)]] float sunScale;
    [[vk::offset(1048)]] float tanHalfFieldOfView;
    [[vk::offset(1052)]] uint _pad1052;
    [[vk::offset(1056)]] uint2 tileGrid;
    [[vk::offset(1064)]] uint viewBase;
    [[vk::offset(1068)]] uint _pad1068;
    [[vk::offset(1072)]] float3 viewForward;
    [[vk::offset(1084)]] uint _pad1084;
    [[vk::offset(1088)]] float3 viewPosition;
    [[vk::offset(1100)]] uint _pad1100;
    [[vk::offset(1104)]] float3 viewRight;
    [[vk::offset(1116)]] uint _pad1116;
    [[vk::offset(1120)]] float3 viewUp;
    [[vk::offset(1132)]] uint viewportCount;
    [[vk::offset(1136)]] uint workCounterRow;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfWorldPass> passGroupIsaE5CD1162 : register(b0, space3);
#define passGroup passGroupIsaE5CD1162
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfInstanceMasks : register(t1, space3);
[[vk::binding(2, 3)]] RWStructuredBuffer<uint> sdfInstanceMasksRW : register(u2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<float> tiles : register(t3, space3);
[[vk::binding(4, 3)]] RWStructuredBuffer<float> tilesRW : register(u4, space3);
[[vk::binding(5, 3)]] StructuredBuffer<uint> cullBounds : register(t5, space3);
[[vk::binding(6, 3)]] RWStructuredBuffer<uint> cullBoundsRW : register(u6, space3);
[[vk::binding(7, 3)]] RWStructuredBuffer<uint> viewsArgsRW : register(u7, space3);
[[vk::binding(8, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t8, space3);
[[vk::binding(9, 3)]] RWStructuredBuffer<uint> sdfVisibilityRecordsRW : register(u9, space3);
[[vk::binding(10, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> output : register(u10, space3);
[[vk::binding(11, 3)]] Texture2D<float4> screenSources[32] : register(t11, space3);
[[vk::binding(43, 3)]] Texture2D<float4> meshVisibility : register(t43, space3);
[[vk::binding(44, 3)]] RWStructuredBuffer<uint> workCounters : register(u44, space3);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps then texels written, as a 64-bit count in
// two words, low word first. A kernel that also compiles under an interface declaring no work counters (a
// document pass's) counts inside #if defined(PUCK_WORK_COUNTERS).
#define PUCK_WORK_COUNTERS 1
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

#endif // PUCK_SHADER_INTERFACE_SDF_WORLD
