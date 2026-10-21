// Generated from shader interface 'sdf-mesh' (sha256/f7ac80ebe27efe0e1317c3bb77892e2d5de04e942fda4b830a619add10573b8d). Regenerate it from the interface; never edit it.
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
    [[vk::offset(288)]] uint lightCount;
    [[vk::offset(292)]] uint meshBakeCount;
    [[vk::offset(296)]] uint _pad296;
    [[vk::offset(300)]] uint _pad300;
    [[vk::offset(304)]] float4 meshBakeLattices[64];
    [[vk::offset(1328)]] uint meshDraws;
    [[vk::offset(1332)]] uint _pad1332;
    [[vk::offset(1336)]] uint _pad1336;
    [[vk::offset(1340)]] uint _pad1340;
    [[vk::offset(1344)]] int4 meshIrradianceLow;
    [[vk::offset(1360)]] uint4 meshIrradianceVolume;
    [[vk::offset(1376)]] float4 meshShadowCascades[12];
    [[vk::offset(1568)]] uint meshShadowCount;
    [[vk::offset(1572)]] uint _pad1572;
    [[vk::offset(1576)]] uint _pad1576;
    [[vk::offset(1580)]] uint _pad1580;
    [[vk::offset(1584)]] float4 meshShadowParameters;
    [[vk::offset(1600)]] uint meshShadowTexels;
    [[vk::offset(1604)]] float nearDistance;
    [[vk::offset(1608)]] uint _pad1608;
    [[vk::offset(1612)]] uint _pad1612;
    [[vk::offset(1616)]] float4 previousView[6];
    [[vk::offset(1712)]] uint screenCount;
    [[vk::offset(1716)]] float screenFidelity;
    [[vk::offset(1720)]] uint shadowFadeCount;
    [[vk::offset(1724)]] uint shadowSlotCount;
    [[vk::offset(1728)]] int4 shadowSlots;
    [[vk::offset(1744)]] float tanHalfFieldOfView;
    [[vk::offset(1748)]] uint temporal;
    [[vk::offset(1752)]] uint viewBase;
    [[vk::offset(1756)]] uint _pad1756;
    [[vk::offset(1760)]] float3 viewForward;
    [[vk::offset(1772)]] uint _pad1772;
    [[vk::offset(1776)]] float3 viewPosition;
    [[vk::offset(1788)]] uint _pad1788;
    [[vk::offset(1792)]] float3 viewRight;
    [[vk::offset(1804)]] uint _pad1804;
    [[vk::offset(1808)]] float3 viewUp;
    [[vk::offset(1820)]] uint viewportCount;
    [[vk::offset(1824)]] uint workCounterRow;
    [[vk::offset(1828)]] uint workCounterRowDetail;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfMeshPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t2, space3);
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
