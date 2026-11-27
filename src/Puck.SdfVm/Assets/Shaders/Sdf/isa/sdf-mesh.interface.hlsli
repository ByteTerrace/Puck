// Generated from shader interface 'sdf-mesh' (sha256/4a01c8d11d79f9ccbf50fa05d3210db72e8b8c7c03137a88b17977f5e7d7f3f4). Regenerate it from the interface; never edit it.
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
    [[vk::offset(60)]] uint _pad60;
    [[vk::offset(64)]] float4 debugOptions;
    [[vk::offset(80)]] uint disableAmbientOcclusion;
    [[vk::offset(84)]] uint disableScreenLights;
    [[vk::offset(88)]] uint disableSoftShadows;
    [[vk::offset(92)]] uint _pad92;
    [[vk::offset(96)]] float2 displayPixels;
    [[vk::offset(104)]] float exposeShare;
    [[vk::offset(108)]] float exposure;
    [[vk::offset(112)]] float farDistance;
    [[vk::offset(116)]] uint _pad116;
    [[vk::offset(120)]] uint _pad120;
    [[vk::offset(124)]] uint _pad124;
    [[vk::offset(128)]] float4 fieldSliceAxisU;
    [[vk::offset(144)]] float4 fieldSliceAxisV;
    [[vk::offset(160)]] float4 fieldSliceOrigin;
    [[vk::offset(176)]] float flowClock;
    [[vk::offset(180)]] uint _pad180;
    [[vk::offset(184)]] float2 frustumOffset;
    [[vk::offset(192)]] uint geometryOnly;
    [[vk::offset(196)]] uint gridFlags;
    [[vk::offset(200)]] float gridLineWidth;
    [[vk::offset(204)]] uint _pad204;
    [[vk::offset(208)]] float4 gridObjectFrame;
    [[vk::offset(224)]] float3 gridObjectOrigin;
    [[vk::offset(236)]] float gridObjectPatchRadius;
    [[vk::offset(240)]] float3 gridObjectPitch;
    [[vk::offset(252)]] float gridPlaneY;
    [[vk::offset(256)]] float4 gridWorldFrame;
    [[vk::offset(272)]] float3 gridWorldOrigin;
    [[vk::offset(284)]] uint _pad284;
    [[vk::offset(288)]] float3 gridWorldPitch;
    [[vk::offset(300)]] uint historyFrames;
    [[vk::offset(304)]] uint2 imageExtent;
    [[vk::offset(312)]] uint _pad312;
    [[vk::offset(316)]] uint _pad316;
    [[vk::offset(320)]] float4 indirectApply;
    [[vk::offset(336)]] float indirectContact;
    [[vk::offset(340)]] uint _pad340;
    [[vk::offset(344)]] uint _pad344;
    [[vk::offset(348)]] uint _pad348;
    [[vk::offset(352)]] float4 indirectSourceGains;
    [[vk::offset(368)]] uint indirectSources;
    [[vk::offset(372)]] uint inkEdges;
    [[vk::offset(376)]] float2 jitter;
    [[vk::offset(384)]] uint lightCount;
    [[vk::offset(388)]] uint _pad388;
    [[vk::offset(392)]] uint _pad392;
    [[vk::offset(396)]] uint _pad396;
    [[vk::offset(400)]] uint4 lightMask;
    [[vk::offset(416)]] float lightScale;
    [[vk::offset(420)]] float localAdaptation;
    [[vk::offset(424)]] uint meshDraws;
    [[vk::offset(428)]] uint _pad428;
    [[vk::offset(432)]] uint2 meshIrradianceGeometry;
    [[vk::offset(440)]] uint _pad440;
    [[vk::offset(444)]] uint _pad444;
    [[vk::offset(448)]] int4 meshIrradianceLevels[3];
    [[vk::offset(496)]] float4 meshIrradianceSpacings[3];
    [[vk::offset(544)]] float4 meshShadowCascades[16];
    [[vk::offset(800)]] uint meshShadowCount;
    [[vk::offset(804)]] uint _pad804;
    [[vk::offset(808)]] uint _pad808;
    [[vk::offset(812)]] uint _pad812;
    [[vk::offset(816)]] float4 meshShadowParameters;
    [[vk::offset(832)]] uint4 meshShadowRoles;
    [[vk::offset(848)]] float4 meshShadowSlopes;
    [[vk::offset(864)]] uint4 meshShadowTexels;
    [[vk::offset(880)]] float4 meterLight[6];
    [[vk::offset(976)]] float meterLuminance;
    [[vk::offset(980)]] float nearDistance;
    [[vk::offset(984)]] uint _pad984;
    [[vk::offset(988)]] uint _pad988;
    [[vk::offset(992)]] uint3 poseOverlay;
    [[vk::offset(1004)]] uint _pad1004;
    [[vk::offset(1008)]] float4 previousView[6];
    [[vk::offset(1104)]] uint reflectionSteps;
    [[vk::offset(1108)]] uint screenCount;
    [[vk::offset(1112)]] float screenFidelity;
    [[vk::offset(1116)]] uint screenReflections;
    [[vk::offset(1120)]] float4 screenWindows[128];
    [[vk::offset(3168)]] uint4 shadeBinWidths;
    [[vk::offset(3184)]] uint4 shadeDefault;
    [[vk::offset(3200)]] float4 shadeDemand;
    [[vk::offset(3216)]] uint4 shadeIdentity[2];
    [[vk::offset(3248)]] uint shadePrograms;
    [[vk::offset(3252)]] uint shadowFadeCount;
    [[vk::offset(3256)]] uint _pad3256;
    [[vk::offset(3260)]] uint _pad3260;
    [[vk::offset(3264)]] int4 shadowRoles;
    [[vk::offset(3280)]] uint shadowSlotCount;
    [[vk::offset(3284)]] uint _pad3284;
    [[vk::offset(3288)]] uint _pad3288;
    [[vk::offset(3292)]] uint _pad3292;
    [[vk::offset(3296)]] int4 shadowSlots;
    [[vk::offset(3312)]] float skyPeakLuminance;
    [[vk::offset(3316)]] uint _pad3316;
    [[vk::offset(3320)]] uint _pad3320;
    [[vk::offset(3324)]] uint _pad3324;
    [[vk::offset(3328)]] float3 stampFade;
    [[vk::offset(3340)]] float tanHalfFieldOfView;
    [[vk::offset(3344)]] uint temporal;
    [[vk::offset(3348)]] float toonMode;
    [[vk::offset(3352)]] uint viewBase;
    [[vk::offset(3356)]] uint _pad3356;
    [[vk::offset(3360)]] float3 viewForward;
    [[vk::offset(3372)]] uint _pad3372;
    [[vk::offset(3376)]] float3 viewPosition;
    [[vk::offset(3388)]] uint _pad3388;
    [[vk::offset(3392)]] float3 viewRight;
    [[vk::offset(3404)]] uint _pad3404;
    [[vk::offset(3408)]] float3 viewUp;
    [[vk::offset(3420)]] uint viewportCount;
    [[vk::offset(3424)]] uint4 viewsRate;
    [[vk::offset(3440)]] uint workCounterRow;
    [[vk::offset(3444)]] uint workCounterRowDetail;
    [[vk::offset(3448)]] uint _pad3448;
    [[vk::offset(3452)]] uint _pad3452;
    [[vk::offset(3456)]] float4 worldLanes;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfMeshPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<uint> sdfFrameSampled : register(t3, space3);
[[vk::binding(4, 3)]] RWStructuredBuffer<uint> workCounters : register(u4, space3);
[[vk::binding(5, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t5, space3);
[[vk::binding(6, 3)]] RWStructuredBuffer<uint> sdfGlassSumsRW : register(u6, space3);
[[vk::binding(7, 3)]] RWStructuredBuffer<uint64_t> sdfGlassLayersRW : register(u7, space3);
[[vk::binding(8, 3)]] Texture2D<float4> meshVisibility : register(t8, space3);
[[vk::binding(9, 3)]] StructuredBuffer<uint4> sdfMeshOcclusionCandidates : register(t9, space3);
[[vk::binding(10, 3)]] RWStructuredBuffer<uint4> sdfMeshOcclusionVisibleRW : register(u10, space3);
[[vk::binding(11, 3)]] RWStructuredBuffer<uint> sdfMeshOcclusionDrawsRW : register(u11, space3);
[[vk::binding(12, 3)]] RWStructuredBuffer<uint> sdfMeshOcclusionCountsRW : register(u12, space3);
[[vk::binding(13, 3)]] RWStructuredBuffer<uint> sdfMeshOcclusionDepthRW : register(u13, space3);
[[vk::binding(14, 3)]] StructuredBuffer<uint> sdfMeshIndirectCommands : register(t14, space3);
[[vk::binding(15, 3)]] StructuredBuffer<uint4> sdfWords : register(t15, space3);
[[vk::binding(16, 3)]] StructuredBuffer<uint> sdfMeshDraws : register(t16, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfMeshPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfMeshPushedIndex> pushedIndex : register(b0, space4);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, shape evaluations, shape gradients, rejected mesh hits and light visits, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 18u;
static const uint PuckWorkDisabledRow = 0xFFFFFFFFu;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShapesWord = 10u;
static const uint PuckWorkGradientsWord = 12u;
static const uint PuckWorkRejectedHitsWord = 14u;
static const uint PuckWorkLightsWord = 16u;
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
// Adds the lights a lane's shading walks visited to its pass's row: the wave sums its lanes and the first adds
// the sum. Every lane of the wave must reach the call.
void puckCountLights(uint visits) {
    if (passGroup.workCounterRow == PuckWorkDisabledRow) {
        return;
    }
    uint waveVisits = WaveActiveSum(visits);

    if (WaveIsFirstLane()) {
        puckAddRowWork(PuckWorkLightsWord, waveVisits);
    }
}

#endif // PUCK_SHADER_INTERFACE_SDF_MESH
