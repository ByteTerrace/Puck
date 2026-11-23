// Generated from shader interface 'sdf-mesh' (sha256/d90e5141e68a292cc224c94f31ebe279624f41c85cb4a100955c1d9480595b4b). Regenerate it from the interface; never edit it.
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
    [[vk::offset(72)]] float2 displayPixels;
    [[vk::offset(80)]] float exposeShare;
    [[vk::offset(84)]] float exposure;
    [[vk::offset(88)]] float farDistance;
    [[vk::offset(92)]] float flowClock;
    [[vk::offset(96)]] float2 frustumOffset;
    [[vk::offset(104)]] uint geometryOnly;
    [[vk::offset(108)]] uint gridFlags;
    [[vk::offset(112)]] float gridLineWidth;
    [[vk::offset(116)]] uint _pad116;
    [[vk::offset(120)]] uint _pad120;
    [[vk::offset(124)]] uint _pad124;
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
    [[vk::offset(232)]] uint _pad232;
    [[vk::offset(236)]] uint _pad236;
    [[vk::offset(240)]] float4 indirectApply;
    [[vk::offset(256)]] float indirectContact;
    [[vk::offset(260)]] uint _pad260;
    [[vk::offset(264)]] uint _pad264;
    [[vk::offset(268)]] uint _pad268;
    [[vk::offset(272)]] float4 indirectSourceGains;
    [[vk::offset(288)]] uint indirectSources;
    [[vk::offset(292)]] uint inkEdges;
    [[vk::offset(296)]] float2 jitter;
    [[vk::offset(304)]] uint lightCount;
    [[vk::offset(308)]] uint _pad308;
    [[vk::offset(312)]] uint _pad312;
    [[vk::offset(316)]] uint _pad316;
    [[vk::offset(320)]] uint4 lightMask;
    [[vk::offset(336)]] float localAdaptation;
    [[vk::offset(340)]] uint meshDraws;
    [[vk::offset(344)]] uint2 meshIrradianceGeometry;
    [[vk::offset(352)]] int4 meshIrradianceLevels[3];
    [[vk::offset(400)]] float4 meshIrradianceSpacings[3];
    [[vk::offset(448)]] float4 meshShadowCascades[16];
    [[vk::offset(704)]] uint meshShadowCount;
    [[vk::offset(708)]] uint _pad708;
    [[vk::offset(712)]] uint _pad712;
    [[vk::offset(716)]] uint _pad716;
    [[vk::offset(720)]] float4 meshShadowParameters;
    [[vk::offset(736)]] uint4 meshShadowRoles;
    [[vk::offset(752)]] float4 meshShadowSlopes;
    [[vk::offset(768)]] uint4 meshShadowTexels;
    [[vk::offset(784)]] float4 meterLight[6];
    [[vk::offset(880)]] float meterLuminance;
    [[vk::offset(884)]] float nearDistance;
    [[vk::offset(888)]] uint _pad888;
    [[vk::offset(892)]] uint _pad892;
    [[vk::offset(896)]] uint3 poseOverlay;
    [[vk::offset(908)]] uint _pad908;
    [[vk::offset(912)]] float4 previousView[6];
    [[vk::offset(1008)]] uint screenCount;
    [[vk::offset(1012)]] float screenFidelity;
    [[vk::offset(1016)]] uint _pad1016;
    [[vk::offset(1020)]] uint _pad1020;
    [[vk::offset(1024)]] float4 screenWindows[32];
    [[vk::offset(1536)]] uint4 shadeBinWidths;
    [[vk::offset(1552)]] uint4 shadeDefault;
    [[vk::offset(1568)]] float4 shadeDemand;
    [[vk::offset(1584)]] uint4 shadeIdentity[2];
    [[vk::offset(1616)]] uint shadePrograms;
    [[vk::offset(1620)]] uint shadowFadeCount;
    [[vk::offset(1624)]] uint _pad1624;
    [[vk::offset(1628)]] uint _pad1628;
    [[vk::offset(1632)]] int4 shadowRoles;
    [[vk::offset(1648)]] uint shadowSlotCount;
    [[vk::offset(1652)]] uint _pad1652;
    [[vk::offset(1656)]] uint _pad1656;
    [[vk::offset(1660)]] uint _pad1660;
    [[vk::offset(1664)]] int4 shadowSlots;
    [[vk::offset(1680)]] float skyPeakLuminance;
    [[vk::offset(1684)]] uint _pad1684;
    [[vk::offset(1688)]] uint _pad1688;
    [[vk::offset(1692)]] uint _pad1692;
    [[vk::offset(1696)]] float3 stampFade;
    [[vk::offset(1708)]] float tanHalfFieldOfView;
    [[vk::offset(1712)]] uint temporal;
    [[vk::offset(1716)]] float toonMode;
    [[vk::offset(1720)]] uint viewBase;
    [[vk::offset(1724)]] uint _pad1724;
    [[vk::offset(1728)]] float3 viewForward;
    [[vk::offset(1740)]] uint _pad1740;
    [[vk::offset(1744)]] float3 viewPosition;
    [[vk::offset(1756)]] uint _pad1756;
    [[vk::offset(1760)]] float3 viewRight;
    [[vk::offset(1772)]] uint _pad1772;
    [[vk::offset(1776)]] float3 viewUp;
    [[vk::offset(1788)]] uint viewportCount;
    [[vk::offset(1792)]] uint4 viewsRate;
    [[vk::offset(1808)]] uint workCounterRow;
    [[vk::offset(1812)]] uint workCounterRowDetail;
    [[vk::offset(1816)]] uint _pad1816;
    [[vk::offset(1820)]] uint _pad1820;
    [[vk::offset(1824)]] float4 worldLanes;
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
