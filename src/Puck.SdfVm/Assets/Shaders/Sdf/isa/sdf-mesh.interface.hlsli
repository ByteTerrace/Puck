// Generated from shader interface 'sdf-mesh' (sha256/bd7e00eed594b89c9dd96886a47efbfe4784496730320283527fc1a2e0eeff6b). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_MESH
#define PUCK_SHADER_INTERFACE_SDF_MESH

// The Pass group: descriptor set 3, register space 3.
struct SdfMeshPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float aspectRatio;
    [[vk::offset(12)]] float curvatureCavity;
    [[vk::offset(16)]] float curvatureInk;
    [[vk::offset(20)]] uint _pad20;
    [[vk::offset(24)]] uint _pad24;
    [[vk::offset(28)]] uint _pad28;
    [[vk::offset(32)]] float3 curvatureInkColor;
    [[vk::offset(44)]] float curvatureInkHigh;
    [[vk::offset(48)]] float curvatureInkLow;
    [[vk::offset(52)]] float curvatureRim;
    [[vk::offset(56)]] uint debugMode;
    [[vk::offset(60)]] uint disableAmbientOcclusion;
    [[vk::offset(64)]] uint disableScreenLights;
    [[vk::offset(68)]] uint disableSoftShadows;
    [[vk::offset(72)]] float farDistance;
    [[vk::offset(76)]] uint _pad76;
    [[vk::offset(80)]] float2 frustumOffset;
    [[vk::offset(88)]] uint geometryOnly;
    [[vk::offset(92)]] uint gridFlags;
    [[vk::offset(96)]] float gridLineWidth;
    [[vk::offset(100)]] uint _pad100;
    [[vk::offset(104)]] uint _pad104;
    [[vk::offset(108)]] uint _pad108;
    [[vk::offset(112)]] float4 gridObjectFrame;
    [[vk::offset(128)]] float3 gridObjectOrigin;
    [[vk::offset(140)]] float gridObjectPatchRadius;
    [[vk::offset(144)]] float3 gridObjectPitch;
    [[vk::offset(156)]] float gridPlaneY;
    [[vk::offset(160)]] float4 gridWorldFrame;
    [[vk::offset(176)]] float3 gridWorldOrigin;
    [[vk::offset(188)]] uint _pad188;
    [[vk::offset(192)]] float3 gridWorldPitch;
    [[vk::offset(204)]] uint historyFrames;
    [[vk::offset(208)]] uint2 imageExtent;
    [[vk::offset(216)]] uint _pad216;
    [[vk::offset(220)]] uint _pad220;
    [[vk::offset(224)]] float4 indirectApply;
    [[vk::offset(240)]] float indirectContact;
    [[vk::offset(244)]] uint _pad244;
    [[vk::offset(248)]] uint _pad248;
    [[vk::offset(252)]] uint _pad252;
    [[vk::offset(256)]] float4 indirectSourceGains;
    [[vk::offset(272)]] uint indirectSources;
    [[vk::offset(276)]] uint instanceMaskWordCount;
    [[vk::offset(280)]] float2 jitter;
    [[vk::offset(288)]] uint lightCamera;
    [[vk::offset(292)]] uint lightCount;
    [[vk::offset(296)]] uint meshBakeCount;
    [[vk::offset(300)]] uint _pad300;
    [[vk::offset(304)]] float4 meshBakeLattices[48];
    [[vk::offset(1072)]] uint meshDraws;
    [[vk::offset(1076)]] uint _pad1076;
    [[vk::offset(1080)]] uint _pad1080;
    [[vk::offset(1084)]] uint _pad1084;
    [[vk::offset(1088)]] int4 meshIrradianceLow;
    [[vk::offset(1104)]] uint4 meshIrradianceVolume;
    [[vk::offset(1120)]] float4 meshShadowCascades[12];
    [[vk::offset(1312)]] uint meshShadowCount;
    [[vk::offset(1316)]] uint _pad1316;
    [[vk::offset(1320)]] uint _pad1320;
    [[vk::offset(1324)]] uint _pad1324;
    [[vk::offset(1328)]] float4 meshShadowParameters;
    [[vk::offset(1344)]] float nearDistance;
    [[vk::offset(1348)]] uint _pad1348;
    [[vk::offset(1352)]] uint _pad1352;
    [[vk::offset(1356)]] uint _pad1356;
    [[vk::offset(1360)]] float4 previousView[6];
    [[vk::offset(1456)]] uint screenCount;
    [[vk::offset(1460)]] uint shadowFadeCount;
    [[vk::offset(1464)]] uint shadowSlotCount;
    [[vk::offset(1468)]] uint _pad1468;
    [[vk::offset(1472)]] int4 shadowSlots;
    [[vk::offset(1488)]] float tanHalfFieldOfView;
    [[vk::offset(1492)]] uint temporal;
    [[vk::offset(1496)]] uint viewBase;
    [[vk::offset(1500)]] uint _pad1500;
    [[vk::offset(1504)]] float3 viewForward;
    [[vk::offset(1516)]] uint _pad1516;
    [[vk::offset(1520)]] float3 viewPosition;
    [[vk::offset(1532)]] uint _pad1532;
    [[vk::offset(1536)]] float3 viewRight;
    [[vk::offset(1548)]] uint _pad1548;
    [[vk::offset(1552)]] float3 viewUp;
    [[vk::offset(1564)]] uint viewportCount;
    [[vk::offset(1568)]] uint workCounterRow;
    [[vk::offset(1572)]] uint workCounterRowDetail;
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
// sky texture loads, shape evaluations and shape gradients, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 14u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShapesWord = 10u;
static const uint PuckWorkGradientsWord = 12u;
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
void puckCountShapes(uint shapes, uint gradients) {
    uint waveShapes = WaveActiveSum(shapes);
    uint waveGradients = WaveActiveSum(gradients);
    if (WaveIsFirstLane()) {
        uint row = passGroup.workCounterRow * PuckWorkRowWords;
        puckAddWork(row + PuckWorkShapesWord, waveShapes);
        puckAddWork(row + PuckWorkGradientsWord, waveGradients);
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
