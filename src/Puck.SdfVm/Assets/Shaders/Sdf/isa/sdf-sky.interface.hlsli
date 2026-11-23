// Generated from shader interface 'sdf-sky' (sha256/8f2ea72e04401fa38e7492f38b3c28379d9eeb92baf688cb54aa004a906b32c3). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_SKY
#define PUCK_SHADER_INTERFACE_SDF_SKY

struct SdfLight {
    [[vk::offset(0)]] float3 Direction;
    [[vk::offset(12)]] float Weight;
    [[vk::offset(16)]] float3 Color;
    [[vk::offset(28)]] uint Kind;
    [[vk::offset(32)]] float Param;
    [[vk::offset(36)]] uint Shadows;
    [[vk::offset(40)]] int DynamicSlot;
    [[vk::offset(44)]] float Bounce;
    [[vk::offset(48)]] float3 SpotAxis;
    [[vk::offset(60)]] float SpotOffset;
};

struct SdfSkyBlock {
    [[vk::offset(0)]] uint AirFlags;
    [[vk::offset(4)]] uint LayerCount;
    [[vk::offset(8)]] float Ambient;
    [[vk::offset(12)]] uint Quality;
    [[vk::offset(16)]] float3 FrameRight;
    [[vk::offset(28)]] uint BaseRun;
    [[vk::offset(32)]] float3 FrameUp;
    [[vk::offset(44)]] uint UpperRuns;
    [[vk::offset(48)]] float3 FrameForward;
    [[vk::offset(60)]] float Reflection;
    [[vk::offset(64)]] uint AirLightCount;
    [[vk::offset(68)]] float FogExtinction;
    [[vk::offset(72)]] float FogBase;
    [[vk::offset(76)]] float FogFalloff;
    [[vk::offset(80)]] float3 FogColor;
    [[vk::offset(92)]] float MediumExtinction;
    [[vk::offset(96)]] float3 MediumColor;
    [[vk::offset(108)]] float MediumSurface;
    [[vk::offset(112)]] float HazeExtinction;
    [[vk::offset(116)]] float HazeBase;
    [[vk::offset(120)]] float HazeFalloff;
    [[vk::offset(124)]] float HazeAnisotropy;
    [[vk::offset(128)]] float3 AirLight0Direction;
    [[vk::offset(140)]] uint _pad140;
    [[vk::offset(144)]] float3 AirLight0Radiance;
    [[vk::offset(156)]] uint _pad156;
    [[vk::offset(160)]] float3 AirLight1Direction;
    [[vk::offset(172)]] uint _pad172;
    [[vk::offset(176)]] float3 AirLight1Radiance;
    [[vk::offset(188)]] uint _pad188;
    [[vk::offset(192)]] float3 AirLight2Direction;
    [[vk::offset(204)]] uint _pad204;
    [[vk::offset(208)]] float3 AirLight2Radiance;
    [[vk::offset(220)]] uint _pad220;
    [[vk::offset(224)]] float3 AirLight3Direction;
    [[vk::offset(236)]] uint _pad236;
    [[vk::offset(240)]] float3 AirLight3Radiance;
    [[vk::offset(252)]] uint _pad252;
};

struct SdfSkyLayer {
    [[vk::offset(0)]] uint Kind;
    [[vk::offset(4)]] uint Blend;
    [[vk::offset(8)]] uint Detail;
    [[vk::offset(12)]] uint Visibility;
    [[vk::offset(16)]] float Opacity;
    [[vk::offset(20)]] uint Mask;
    [[vk::offset(24)]] float MaskSoftness;
    [[vk::offset(28)]] float Phase;
    [[vk::offset(32)]] float4 MaskBand;
    [[vk::offset(48)]] float4 Rotation;
    [[vk::offset(64)]] float4 P0;
    [[vk::offset(80)]] float4 P1;
    [[vk::offset(96)]] float4 P2;
    [[vk::offset(112)]] float4 P3;
    [[vk::offset(128)]] float4 P4;
    [[vk::offset(144)]] float4 P5;
    [[vk::offset(160)]] float4 P6;
    [[vk::offset(176)]] float4 P7;
};

struct SdfShadowHandoff {
    [[vk::offset(0)]] int Outgoing;
    [[vk::offset(4)]] int Incoming;
    [[vk::offset(8)]] int Slot;
    [[vk::offset(12)]] float Weight;
};

// The Frame group: descriptor set 0, register space 0.
struct SdfSkyFrame {
    [[vk::offset(0)]] float2 pointer;
    [[vk::offset(8)]] uint2 tick;
    [[vk::offset(16)]] float time;
    [[vk::offset(20)]] float timeDelta;
    [[vk::offset(24)]] uint frame;
    [[vk::offset(28)]] uint tickRate;
    [[vk::offset(32)]] uint pointerDown;
};
[[vk::binding(0, 0)]] ConstantBuffer<SdfSkyFrame> frameGroup : register(b0, space0);

// The World group: descriptor set 1, register space 1.
[[vk::binding(0, 1)]] StructuredBuffer<uint4> sdfWords : register(t0, space1);
[[vk::binding(1, 1)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t1, space1);
[[vk::binding(2, 1)]] StructuredBuffer<uint> sdfSampledValues : register(t2, space1);
[[vk::binding(3, 1)]] StructuredBuffer<float4> sdfMeshBakePoints : register(t3, space1);
[[vk::binding(4, 1)]] Texture2D<float4> sdfGlyphAtlas : register(t4, space1);
[[vk::binding(5, 1)]] SamplerState samplers[2] : register(s5, space1);
[[vk::binding(7, 1)]] StructuredBuffer<float4> sdfPreviousDynamicTransforms : register(t7, space1);
[[vk::binding(8, 1)]] StructuredBuffer<uint> sdfPreviousMeshTransforms : register(t8, space1);
[[vk::binding(9, 1)]] StructuredBuffer<float4> screenSurfaces : register(t9, space1);
[[vk::binding(10, 1)]] StructuredBuffer<float4> screenMappings : register(t10, space1);
[[vk::binding(11, 1)]] StructuredBuffer<float4> sdfScreenLights : register(t11, space1);
[[vk::binding(12, 1)]] StructuredBuffer<uint4> sdfDecalCells : register(t12, space1);
[[vk::binding(13, 1)]] StructuredBuffer<float4> sdfVolumes : register(t13, space1);
[[vk::binding(14, 1)]] StructuredBuffer<uint> sdfMeshRegion : register(t14, space1);
[[vk::binding(15, 1)]] StructuredBuffer<uint> sdfMeshIrradiance : register(t15, space1);
[[vk::binding(16, 1)]] StructuredBuffer<uint> sdfFrameSampled : register(t16, space1);
[[vk::binding(17, 1)]] StructuredBuffer<SdfLight> sdfLightsLayout27ca437eafcfb2ef7e652cdc81f326face73209e6adffa61f04dffc997d7c8c5 : register(t17, space1);
#define sdfLights sdfLightsLayout27ca437eafcfb2ef7e652cdc81f326face73209e6adffa61f04dffc997d7c8c5
[[vk::binding(18, 1)]] StructuredBuffer<SdfSkyBlock> sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9 : register(t18, space1);
#define sdfSky sdfSkyLayoutcf5989bcab395177650d42c028df69d633f031173a236e87e6f40e104d525aa9
[[vk::binding(19, 1)]] StructuredBuffer<SdfSkyLayer> sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8 : register(t19, space1);
#define sdfSkyLayers sdfSkyLayersLayoutb8b05e94adeef57d901b9217964b288f590ab8e7f74318d31922800728b169a8
[[vk::binding(20, 1)]] StructuredBuffer<float4> sdfSkyCoefficients : register(t20, space1);
[[vk::binding(21, 1)]] StructuredBuffer<SdfShadowHandoff> sdfShadowHandoffsLayout981efb60b212c94dac2862f0fb486ea3aaed2504fac947a2a5d6ffc91403e13e : register(t21, space1);
#define sdfShadowHandoffs sdfShadowHandoffsLayout981efb60b212c94dac2862f0fb486ea3aaed2504fac947a2a5d6ffc91403e13e
[[vk::binding(22, 1)]] StructuredBuffer<uint2> sdfSkyEnvironment : register(t22, space1);

// The Pass group: descriptor set 3, register space 3.
struct SdfSkyPass {
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
    [[vk::offset(88)]] float exposureCenter;
    [[vk::offset(92)]] float farDistance;
    [[vk::offset(96)]] float flowClock;
    [[vk::offset(100)]] uint _pad100;
    [[vk::offset(104)]] float2 frustumOffset;
    [[vk::offset(112)]] uint geometryOnly;
    [[vk::offset(116)]] uint gridFlags;
    [[vk::offset(120)]] float gridLineWidth;
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
    [[vk::offset(1008)]] uint resolvedSurface;
    [[vk::offset(1012)]] uint screenCount;
    [[vk::offset(1016)]] float screenFidelity;
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
    [[vk::offset(1680)]] uint2 skyFieldExtent;
    [[vk::offset(1688)]] float skyPeakLuminance;
    [[vk::offset(1692)]] uint _pad1692;
    [[vk::offset(1696)]] float4 skyViews[40];
    [[vk::offset(2336)]] float3 stampFade;
    [[vk::offset(2348)]] float tanHalfFieldOfView;
    [[vk::offset(2352)]] uint temporal;
    [[vk::offset(2356)]] float toonMode;
    [[vk::offset(2360)]] uint viewBase;
    [[vk::offset(2364)]] uint _pad2364;
    [[vk::offset(2368)]] float3 viewForward;
    [[vk::offset(2380)]] uint _pad2380;
    [[vk::offset(2384)]] float3 viewPosition;
    [[vk::offset(2396)]] uint _pad2396;
    [[vk::offset(2400)]] float3 viewRight;
    [[vk::offset(2412)]] uint _pad2412;
    [[vk::offset(2416)]] float3 viewUp;
    [[vk::offset(2428)]] uint viewportCount;
    [[vk::offset(2432)]] uint4 viewsRate;
    [[vk::offset(2448)]] uint workCounterRow;
    [[vk::offset(2452)]] uint workCounterRowDetail;
    [[vk::offset(2456)]] uint _pad2456;
    [[vk::offset(2460)]] uint _pad2460;
    [[vk::offset(2464)]] float4 worldLanes;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfSkyPass> passGroupIsa6295F7F6 : register(b0, space3);
#define passGroup passGroupIsa6295F7F6
[[vk::binding(1, 3)]] Texture2D<float4> skyViewImages[8] : register(t1, space3);
[[vk::binding(9, 3)]] Texture2D<float4> lit : register(t9, space3);
[[vk::binding(10, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t10, space3);
[[vk::binding(11, 3)]] StructuredBuffer<uint2> transport : register(t11, space3);
[[vk::binding(12, 3)]] RWStructuredBuffer<uint> exposureMeterRW : register(u12, space3);
[[vk::binding(13, 3)]] Texture2D<float4> skyBase : register(t13, space3);
[[vk::binding(14, 3)]] Texture2D<float4> skyUpper0 : register(t14, space3);
[[vk::binding(15, 3)]] Texture2D<float4> skyUpper1 : register(t15, space3);
[[vk::binding(16, 3)]] Texture2D<float4> skyUpper2 : register(t16, space3);
[[vk::binding(17, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> skyBaseRW : register(u17, space3);
[[vk::binding(18, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> skyUpper0RW : register(u18, space3);
[[vk::binding(19, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> skyUpper1RW : register(u19, space3);
[[vk::binding(20, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> skyUpper2RW : register(u20, space3);
[[vk::binding(21, 3)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> output : register(u21, space3);
[[vk::binding(22, 3)]] Texture2D<float4> screenSources[32] : register(t22, space3);
[[vk::binding(54, 3)]] RWStructuredBuffer<uint> workCounters : register(u54, space3);

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

#endif // PUCK_SHADER_INTERFACE_SDF_SKY
