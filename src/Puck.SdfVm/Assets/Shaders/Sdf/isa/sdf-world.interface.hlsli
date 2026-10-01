// Generated from shader interface 'sdf-world' (sha256/db0cdc633f8e8311fa15941a15f4219f0f1cc9fa652db82fd7c28ebacc7e388b). Regenerate it from the interface; never edit it.
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
[[vk::binding(2, 1)]] StructuredBuffer<float4> sdfPreviousDynamicTransforms : register(t2, space1);
[[vk::binding(3, 1)]] StructuredBuffer<float4> sdfPreviousMeshTransforms : register(t3, space1);
[[vk::binding(4, 1)]] StructuredBuffer<uint> sdfFrameInstanceGrid : register(t4, space1);
[[vk::binding(5, 1)]] StructuredBuffer<float4> screenSurfaces : register(t5, space1);
[[vk::binding(6, 1)]] StructuredBuffer<float4> screenMappings : register(t6, space1);
[[vk::binding(7, 1)]] StructuredBuffer<float4> sdfScreenLights : register(t7, space1);
[[vk::binding(8, 1)]] StructuredBuffer<uint4> sdfDecalCells : register(t8, space1);
[[vk::binding(9, 1)]] StructuredBuffer<float> sdfBrickPool : register(t9, space1);
[[vk::binding(10, 1)]] StructuredBuffer<float4> sdfVolumes : register(t10, space1);
[[vk::binding(11, 1)]] StructuredBuffer<uint> sdfMeshRegion : register(t11, space1);
[[vk::binding(12, 1)]] Texture2D<float4> sdfGlyphAtlas : register(t12, space1);
[[vk::binding(13, 1)]] SamplerState samplers[2] : register(s13, space1);
[[vk::binding(15, 1)]] Texture2D<float4> sdfMeshAlbedo : register(t15, space1);
[[vk::binding(16, 1)]] Texture2D<float4> sdfMeshNormals : register(t16, space1);
[[vk::binding(17, 1)]] Texture2D<float4> sdfMeshOcclusion : register(t17, space1);
[[vk::binding(18, 1)]] Texture2D<float4> sdfMeshMaterials : register(t18, space1);
[[vk::binding(19, 1)]] Texture2D<float4> sdfMeshEmission : register(t19, space1);

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
    [[vk::offset(992)]] float4 gridWorldFrame;
    [[vk::offset(1008)]] float3 gridWorldOrigin;
    [[vk::offset(1020)]] uint _pad1020;
    [[vk::offset(1024)]] float3 gridWorldPitch;
    [[vk::offset(1036)]] uint historyFrames;
    [[vk::offset(1040)]] uint2 imageExtent;
    [[vk::offset(1048)]] uint instanceMaskWordCount;
    [[vk::offset(1052)]] uint _pad1052;
    [[vk::offset(1056)]] float2 jitter;
    [[vk::offset(1064)]] uint meshDraws;
    [[vk::offset(1068)]] float nearDistance;
    [[vk::offset(1072)]] float4 previousView[6];
    [[vk::offset(1168)]] uint screenCount;
    [[vk::offset(1172)]] float shadowDistanceScale;
    [[vk::offset(1176)]] float sunScale;
    [[vk::offset(1180)]] float tanHalfFieldOfView;
    [[vk::offset(1184)]] uint2 tileGrid;
    [[vk::offset(1192)]] uint viewBase;
    [[vk::offset(1196)]] uint _pad1196;
    [[vk::offset(1200)]] float3 viewForward;
    [[vk::offset(1212)]] uint _pad1212;
    [[vk::offset(1216)]] float3 viewPosition;
    [[vk::offset(1228)]] uint _pad1228;
    [[vk::offset(1232)]] float3 viewRight;
    [[vk::offset(1244)]] uint _pad1244;
    [[vk::offset(1248)]] float3 viewUp;
    [[vk::offset(1260)]] uint viewportCount;
    [[vk::offset(1264)]] uint workCounterRow;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfWorldPass> passGroupIsa1D91FDE2 : register(b0, space3);
#define passGroup passGroupIsa1D91FDE2
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
// two words, low word first. An interface declaring no work counters declares the same two functions empty.
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
