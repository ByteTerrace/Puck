// Generated from shader interface 'sdf-mesh' (sha256/21cbf9a163d1c1291238e818bf38e626e5a0092a1dc4039603051b5583ec1729). Regenerate it from the interface; never edit it.
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
    [[vk::offset(432)]] float4 meshShadowCascades[20];
    [[vk::offset(752)]] uint meshShadowCount;
    [[vk::offset(756)]] uint _pad756;
    [[vk::offset(760)]] uint _pad760;
    [[vk::offset(764)]] uint _pad764;
    [[vk::offset(768)]] float4 meshShadowParameters;
    [[vk::offset(784)]] uint4 meshShadowRoles;
    [[vk::offset(800)]] float4 meshShadowSlopes;
    [[vk::offset(816)]] uint4 meshShadowTexels;
    [[vk::offset(832)]] float4 meterLight[6];
    [[vk::offset(928)]] float meterLuminance;
    [[vk::offset(932)]] float nearDistance;
    [[vk::offset(936)]] uint _pad936;
    [[vk::offset(940)]] uint _pad940;
    [[vk::offset(944)]] uint3 poseOverlay;
    [[vk::offset(956)]] uint _pad956;
    [[vk::offset(960)]] float4 previousView[6];
    [[vk::offset(1056)]] uint screenCount;
    [[vk::offset(1060)]] float screenFidelity;
    [[vk::offset(1064)]] uint _pad1064;
    [[vk::offset(1068)]] uint _pad1068;
    [[vk::offset(1072)]] uint4 shadeBinWidths;
    [[vk::offset(1088)]] uint4 shadeDefault;
    [[vk::offset(1104)]] float4 shadeDemand;
    [[vk::offset(1120)]] uint4 shadeIdentity[2];
    [[vk::offset(1152)]] uint shadePrograms;
    [[vk::offset(1156)]] uint shadowFadeCount;
    [[vk::offset(1160)]] uint _pad1160;
    [[vk::offset(1164)]] uint _pad1164;
    [[vk::offset(1168)]] int4 shadowRoles;
    [[vk::offset(1184)]] uint shadowSlotCount;
    [[vk::offset(1188)]] uint _pad1188;
    [[vk::offset(1192)]] uint _pad1192;
    [[vk::offset(1196)]] uint _pad1196;
    [[vk::offset(1200)]] int4 shadowSlots;
    [[vk::offset(1216)]] float skyPeakLuminance;
    [[vk::offset(1220)]] uint _pad1220;
    [[vk::offset(1224)]] uint _pad1224;
    [[vk::offset(1228)]] uint _pad1228;
    [[vk::offset(1232)]] float3 stampFade;
    [[vk::offset(1244)]] float tanHalfFieldOfView;
    [[vk::offset(1248)]] uint temporal;
    [[vk::offset(1252)]] uint _pad1252;
    [[vk::offset(1256)]] uint _pad1256;
    [[vk::offset(1260)]] uint _pad1260;
    [[vk::offset(1264)]] float4 toonInk;
    [[vk::offset(1280)]] float4 toonOutline;
    [[vk::offset(1296)]] uint viewBase;
    [[vk::offset(1300)]] uint _pad1300;
    [[vk::offset(1304)]] uint _pad1304;
    [[vk::offset(1308)]] uint _pad1308;
    [[vk::offset(1312)]] float3 viewForward;
    [[vk::offset(1324)]] uint _pad1324;
    [[vk::offset(1328)]] float3 viewPosition;
    [[vk::offset(1340)]] uint _pad1340;
    [[vk::offset(1344)]] float3 viewRight;
    [[vk::offset(1356)]] uint _pad1356;
    [[vk::offset(1360)]] float3 viewUp;
    [[vk::offset(1372)]] uint viewportCount;
    [[vk::offset(1376)]] uint4 viewsRate;
    [[vk::offset(1392)]] uint workCounterRow;
    [[vk::offset(1396)]] uint workCounterRowDetail;
    [[vk::offset(1400)]] uint _pad1400;
    [[vk::offset(1404)]] uint _pad1404;
    [[vk::offset(1408)]] float4 worldLanes;
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
// sky texture loads, shape evaluations, shape gradients and rejected mesh hits, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 16u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShapesWord = 10u;
static const uint PuckWorkGradientsWord = 12u;
static const uint PuckWorkRejectedHitsWord = 14u;
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
// Adds one hit the mesh hit door rejected to its pass's row, from the rejecting lane alone: a rejection is rare
// and may sit in divergent control flow, so it takes no wave operation.
void puckCountRejectedHit() {
    puckAddWork(((passGroup.workCounterRow * PuckWorkRowWords) + PuckWorkRejectedHitsWord), 1u);
}

#endif // PUCK_SHADER_INTERFACE_SDF_MESH
