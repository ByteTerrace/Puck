// Generated from shader interface 'sdf-mesh' (sha256/6ceaab0431f79c0067b9ad5c87cdb6946be9b58fdec3aa3ee8d771de110b521d). Regenerate it from the interface; never edit it.
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
    [[vk::offset(1072)]] float4 screenWindows[32];
    [[vk::offset(1584)]] uint4 shadeBinWidths;
    [[vk::offset(1600)]] uint4 shadeDefault;
    [[vk::offset(1616)]] float4 shadeDemand;
    [[vk::offset(1632)]] uint4 shadeIdentity[2];
    [[vk::offset(1664)]] uint shadePrograms;
    [[vk::offset(1668)]] uint shadowFadeCount;
    [[vk::offset(1672)]] uint _pad1672;
    [[vk::offset(1676)]] uint _pad1676;
    [[vk::offset(1680)]] int4 shadowRoles;
    [[vk::offset(1696)]] uint shadowSlotCount;
    [[vk::offset(1700)]] uint _pad1700;
    [[vk::offset(1704)]] uint _pad1704;
    [[vk::offset(1708)]] uint _pad1708;
    [[vk::offset(1712)]] int4 shadowSlots;
    [[vk::offset(1728)]] float skyPeakLuminance;
    [[vk::offset(1732)]] uint _pad1732;
    [[vk::offset(1736)]] uint _pad1736;
    [[vk::offset(1740)]] uint _pad1740;
    [[vk::offset(1744)]] float3 stampFade;
    [[vk::offset(1756)]] float tanHalfFieldOfView;
    [[vk::offset(1760)]] uint temporal;
    [[vk::offset(1764)]] uint _pad1764;
    [[vk::offset(1768)]] uint _pad1768;
    [[vk::offset(1772)]] uint _pad1772;
    [[vk::offset(1776)]] float4 toonInk;
    [[vk::offset(1792)]] float4 toonOutline;
    [[vk::offset(1808)]] uint viewBase;
    [[vk::offset(1812)]] uint _pad1812;
    [[vk::offset(1816)]] uint _pad1816;
    [[vk::offset(1820)]] uint _pad1820;
    [[vk::offset(1824)]] float3 viewForward;
    [[vk::offset(1836)]] uint _pad1836;
    [[vk::offset(1840)]] float3 viewPosition;
    [[vk::offset(1852)]] uint _pad1852;
    [[vk::offset(1856)]] float3 viewRight;
    [[vk::offset(1868)]] uint _pad1868;
    [[vk::offset(1872)]] float3 viewUp;
    [[vk::offset(1884)]] uint viewportCount;
    [[vk::offset(1888)]] uint4 viewsRate;
    [[vk::offset(1904)]] uint workCounterRow;
    [[vk::offset(1908)]] uint workCounterRowDetail;
    [[vk::offset(1912)]] uint _pad1912;
    [[vk::offset(1916)]] uint _pad1916;
    [[vk::offset(1920)]] float4 worldLanes;
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
static const uint PuckWorkDisabledRow = 0xFFFFFFFFu;
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
// Adds to one count of the pass's own row, or nothing while the pass counts nothing. The disabled row's scaled
// words wrap to indices just below the buffer, an out-of-bounds write a Vulkan device without robust buffer
// access faults on, so every lane's add to the pass row passes this door, which tests the row before scaling.
void puckAddRowWork(uint word, uint amount) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
    puckAddWork(((passGroup.workCounterRow * PuckWorkRowWords) + word), amount);
}
#if !defined(__SHADER_TARGET_STAGE) || (__SHADER_TARGET_STAGE == __SHADER_STAGE_COMPUTE)
groupshared uint puckWorkGroupALo;
groupshared uint puckWorkGroupAHi;
groupshared uint puckWorkGroupBLo;
groupshared uint puckWorkGroupBHi;
groupshared uint puckWorkGroupLeader;
// Reduces two counters across a compute thread group in groupshared memory so each group issues at most one
// global atomic per non-zero counter word instead of one per wave. Every thread in the group must reach the call.
void puckAddGroupWork(uint wordA, uint a, uint wordB, uint b) {
    uint waveA = WaveActiveSum(a);
    uint waveB = WaveActiveSum(b);

    if (WaveIsFirstLane()) {
        uint ignored;

        InterlockedExchange(puckWorkGroupALo, 0u, ignored);
        InterlockedExchange(puckWorkGroupAHi, 0u, ignored);
        InterlockedExchange(puckWorkGroupBLo, 0u, ignored);
        InterlockedExchange(puckWorkGroupBHi, 0u, ignored);
        InterlockedExchange(puckWorkGroupLeader, 0u, ignored);
    }
    GroupMemoryBarrierWithGroupSync();

    bool leader = false;

    if (WaveIsFirstLane()) {
        uint prev;

        InterlockedAdd(puckWorkGroupLeader, 1u, prev);
        leader = (prev == 0u);
        if (waveA != 0u) {
            InterlockedAdd(puckWorkGroupALo, waveA, prev);
            if (prev > (0xFFFFFFFFu - waveA)) {
                InterlockedAdd(puckWorkGroupAHi, 1u);
            }
        }
        if (waveB != 0u) {
            InterlockedAdd(puckWorkGroupBLo, waveB, prev);
            if (prev > (0xFFFFFFFFu - waveB)) {
                InterlockedAdd(puckWorkGroupBHi, 1u);
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    uint groupALo = 0u;
    uint groupAHi = 0u;
    uint groupBLo = 0u;
    uint groupBHi = 0u;

    if (leader) {
        groupALo = puckWorkGroupALo;
        groupAHi = puckWorkGroupAHi;
        groupBLo = puckWorkGroupBLo;
        groupBHi = puckWorkGroupBHi;
    }
    GroupMemoryBarrierWithGroupSync();

    if (leader) {
        puckAddWork(wordA, groupALo);
        if (groupAHi != 0u) {
            InterlockedAdd(workCounters[wordA + 1u], groupAHi);
        }
        puckAddWork(wordB, groupBLo);
        if (groupBHi != 0u) {
            InterlockedAdd(workCounters[wordB + 1u], groupBHi);
        }
    }
}
#endif
// Adds an invocation's march steps and texels written to its pass's row: skips uniformly when counting is
// disabled, otherwise reduces across the compute thread group in groupshared memory and adds once per group.
void puckCountWork(uint steps, uint texels) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
#if !defined(__SHADER_TARGET_STAGE) || (__SHADER_TARGET_STAGE == __SHADER_STAGE_COMPUTE)
    uint row = (passGroup.workCounterRow * PuckWorkRowWords);

    puckAddGroupWork((row + PuckWorkStepsWord), steps, (row + PuckWorkTexelsWord), texels);
#endif
}
// Named rows are disjoint from the plain pass row; the ledger sums both once the submission completes.
// The active lanes may name different rows (divergent layer evaluation): each distinct row among them in turn
// sums its lanes' counts across the wave, and that row's first lane adds each sum, so a wave adds once a row
// rather than once a lane.
void puckCountDetail(uint detail, uint steps, uint texels, uint evaluations, uint hashes, uint loads) {
    if (passGroup.workCounterRowDetail == 0u) {
        return;
    }
    [loop] for (;;) {
        if (WaveReadLaneFirst(detail) == detail) {
            uint waveSteps = WaveActiveSum(steps);
            uint waveTexels = WaveActiveSum(texels);
            uint waveEvaluations = WaveActiveSum(evaluations);
            uint waveHashes = WaveActiveSum(hashes);
            uint waveLoads = WaveActiveSum(loads);

            if (WaveIsFirstLane()) {
                uint row = ((passGroup.workCounterRowDetail + detail) * PuckWorkRowWords);
                puckAddWork((row + PuckWorkStepsWord), waveSteps);
                puckAddWork((row + PuckWorkTexelsWord), waveTexels);
                puckAddWork((row + PuckWorkSkyWord), waveEvaluations);
                puckAddWork((row + PuckWorkSkyHashesWord), waveHashes);
                puckAddWork((row + PuckWorkSkyTextureLoadsWord), waveLoads);
            }
            break;
        }
    }
}
void puckCountShapes(uint shapes, uint gradients) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
#if !defined(__SHADER_TARGET_STAGE) || (__SHADER_TARGET_STAGE == __SHADER_STAGE_COMPUTE)
    uint row = (passGroup.workCounterRow * PuckWorkRowWords);

    puckAddGroupWork((row + PuckWorkShapesWord), shapes, (row + PuckWorkGradientsWord), gradients);
#endif
}
// Adds a fragment's march steps and texels written to its pass's row: skips uniformly when counting is disabled,
// otherwise the wave sums its lanes that are not helper lanes, and the first of them adds each sum. A helper
// lane counts nothing and never adds, whether or not the backend lets it take part in wave operations, since its
// atomics have no effect.
void puckCountFragmentWork(uint steps, uint texels) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
    bool counting = !IsHelperLane();
    uint waveSteps = WaveActiveSum(counting ? steps : 0u);
    uint waveTexels = WaveActiveSum(counting ? texels : 0u);

    if (counting && (WavePrefixCountBits(counting) == 0u)) {
        puckAddRowWork(PuckWorkStepsWord, waveSteps);
        puckAddRowWork(PuckWorkTexelsWord, waveTexels);
    }
}
// Adds one hit the mesh hit door rejected to its pass's row, from the rejecting lane alone: a rejection is rare
// and may sit in divergent control flow, so it takes no wave operation.
void puckCountRejectedHit() {
    puckAddRowWork(PuckWorkRejectedHitsWord, 1u);
}
// Adds a lane's sky evaluations and sky texture loads to its pass's row, from the lane alone, since a sky lookup
// sits in divergent shading.
void puckCountSky(uint evaluations, uint loads) {
    puckAddRowWork(PuckWorkSkyWord, evaluations);
    puckAddRowWork(PuckWorkSkyTextureLoadsWord, loads);
}

#endif // PUCK_SHADER_INTERFACE_SDF_MESH
