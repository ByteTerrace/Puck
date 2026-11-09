// Generated from shader interface 'sdf-mesh' (sha256/6ac2edabf1ebdd97aff3e280eafc80fd4cb48cec367a8c2b8e9c56a14c7f249a). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_MESH
#define PUCK_SHADER_INTERFACE_SDF_MESH

// The Pass group: descriptor set 3, register space 3.
struct SdfMeshPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float airLight;
    [[vk::offset(12)]] float aspectRatio;
    [[vk::offset(16)]] float curvatureCavity;
    [[vk::offset(20)]] float curvatureInk;
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
    [[vk::offset(72)]] float exposeShare;
    [[vk::offset(76)]] float exposure;
    [[vk::offset(80)]] float farDistance;
    [[vk::offset(84)]] float flowClock;
    [[vk::offset(88)]] float2 frustumOffset;
    [[vk::offset(96)]] uint geometryOnly;
    [[vk::offset(100)]] uint gridFlags;
    [[vk::offset(104)]] float gridLineWidth;
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
    [[vk::offset(276)]] uint _pad276;
    [[vk::offset(280)]] float2 jitter;
    [[vk::offset(288)]] uint lightCount;
    [[vk::offset(292)]] uint _pad292;
    [[vk::offset(296)]] uint _pad296;
    [[vk::offset(300)]] uint _pad300;
    [[vk::offset(304)]] uint4 lightMask;
    [[vk::offset(320)]] float localAdaptation;
    [[vk::offset(324)]] uint meshDraws;
    [[vk::offset(328)]] uint _pad328;
    [[vk::offset(332)]] uint _pad332;
    [[vk::offset(336)]] int4 meshIrradianceLevels[3];
    [[vk::offset(384)]] float4 meshIrradianceSpacings[3];
    [[vk::offset(432)]] float4 meshShadowCascades[16];
    [[vk::offset(688)]] uint meshShadowCount;
    [[vk::offset(692)]] uint _pad692;
    [[vk::offset(696)]] uint _pad696;
    [[vk::offset(700)]] uint _pad700;
    [[vk::offset(704)]] float4 meshShadowParameters;
    [[vk::offset(720)]] uint meshShadowTexels;
    [[vk::offset(724)]] uint _pad724;
    [[vk::offset(728)]] uint _pad728;
    [[vk::offset(732)]] uint _pad732;
    [[vk::offset(736)]] float4 meterLight[6];
    [[vk::offset(832)]] float meterLuminance;
    [[vk::offset(836)]] float nearDistance;
    [[vk::offset(840)]] uint _pad840;
    [[vk::offset(844)]] uint _pad844;
    [[vk::offset(848)]] float4 previousView[6];
    [[vk::offset(944)]] uint screenCount;
    [[vk::offset(948)]] float screenFidelity;
    [[vk::offset(952)]] uint _pad952;
    [[vk::offset(956)]] uint _pad956;
    [[vk::offset(960)]] uint4 shadeBinWidths;
    [[vk::offset(976)]] uint4 shadeDefault;
    [[vk::offset(992)]] float4 shadeDemand;
    [[vk::offset(1008)]] uint4 shadeIdentity[2];
    [[vk::offset(1040)]] uint shadePrograms;
    [[vk::offset(1044)]] uint shadowFadeCount;
    [[vk::offset(1048)]] uint shadowSlotCount;
    [[vk::offset(1052)]] uint _pad1052;
    [[vk::offset(1056)]] int4 shadowSlots;
    [[vk::offset(1072)]] float skyPeakLuminance;
    [[vk::offset(1076)]] uint _pad1076;
    [[vk::offset(1080)]] uint _pad1080;
    [[vk::offset(1084)]] uint _pad1084;
    [[vk::offset(1088)]] float3 stampFade;
    [[vk::offset(1100)]] float tanHalfFieldOfView;
    [[vk::offset(1104)]] uint temporal;
    [[vk::offset(1108)]] uint _pad1108;
    [[vk::offset(1112)]] uint _pad1112;
    [[vk::offset(1116)]] uint _pad1116;
    [[vk::offset(1120)]] float4 toonBands;
    [[vk::offset(1136)]] float4 toonInk;
    [[vk::offset(1152)]] float4 toonOutline;
    [[vk::offset(1168)]] float4 toonShadow;
    [[vk::offset(1184)]] uint viewBase;
    [[vk::offset(1188)]] uint _pad1188;
    [[vk::offset(1192)]] uint _pad1192;
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
    [[vk::offset(1268)]] uint workCounterRowDetail;
    [[vk::offset(1272)]] uint _pad1272;
    [[vk::offset(1276)]] uint _pad1276;
    [[vk::offset(1280)]] float4 worldLanes;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfMeshPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<uint> sdfFrameSampled : register(t3, space3);
[[vk::binding(4, 3)]] RWStructuredBuffer<uint> workCounters : register(u4, space3);
[[vk::binding(5, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t5, space3);
[[vk::binding(6, 3)]] RWStructuredBuffer<uint> sdfGlassSumsRW : register(u6, space3);
[[vk::binding(7, 3)]] Texture2D<float4> meshVisibility : register(t7, space3);
[[vk::binding(8, 3)]] StructuredBuffer<uint4> sdfMeshOcclusionCandidates : register(t8, space3);
[[vk::binding(9, 3)]] RWStructuredBuffer<uint4> sdfMeshOcclusionVisibleRW : register(u9, space3);
[[vk::binding(10, 3)]] RWStructuredBuffer<uint> sdfMeshOcclusionDrawsRW : register(u10, space3);
[[vk::binding(11, 3)]] RWStructuredBuffer<uint> sdfMeshOcclusionCountsRW : register(u11, space3);
[[vk::binding(12, 3)]] RWStructuredBuffer<uint> sdfMeshOcclusionDepthRW : register(u12, space3);
[[vk::binding(13, 3)]] StructuredBuffer<uint> sdfMeshIndirectCommands : register(t13, space3);
[[vk::binding(14, 3)]] StructuredBuffer<uint4> sdfWords : register(t14, space3);

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
